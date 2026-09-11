#!/usr/bin/env node
/**
 * Open-licence asset fetching.
 *
 * This module is built around refusal, so the refusals are what the test asserts hardest: marketplaces
 * unreachable, http rejected, and an unknown host requiring an explicit licence claim. The happy path then
 * proves a real CC0 download lands in the project with its provenance recorded.
 *
 * Needs network access to Poly Haven's public read-only API. If it is unavailable the network checks report
 * that rather than failing, because an offline machine is not a defect in this code.
 */
import { existsSync, statSync } from "node:fs";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";
import * as openAssets from "../packages/vrcw-mcp/dist/openassets.js";

let passed = 0;
let failed = 0;
let skipped = 0;

async function check(name, fn) {
  try {
    const detail = await fn();
    if (detail && String(detail).startsWith("SKIP")) {
      skipped++;
      console.log(`SKIP  ${name}`);
      console.log(`      ${String(detail).slice(5).trim()}`);
      return;
    }
    passed++;
    console.log(`PASS  ${name}`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 4).join("\n      ")}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function offline(err) {
  return /fetch failed|ENOTFOUND|ETIMEDOUT|EAI_AGAIN|getaddrinfo|network/i.test(String(err.message ?? err));
}

// ---------------------------------------------------------------- refusals

await check("sources are enumerated with their licences", async () => {
  const list = openAssets.listSources();
  assert(list.sources.length >= 2, `too few sources: ${list.sources.length}`);
  assert(list.sources.every((s) => s.licence), "a source has no licence recorded");
  assert(/Booth|Asset Store|marketplace/i.test(String(list.refused)), "marketplaces are not explicitly refused");
  return `${list.sources.map((s) => `${s.id}(${s.licence})`).join(", ")}`;
});

await check("a marketplace host is refused", async () => {
  try {
    await openAssets.fetchOpenAsset("https://booth.pm/some/asset.zip", undefined, undefined, 1024 * 1024);
    throw new Error("expected a refusal");
  } catch (err) {
    if (offline(err)) throw new Error("refusal must not depend on the network");
    assert(/not an allowlisted|marketplace/i.test(err.message), `unhelpful: ${err.message}`);
    return "refused, and explained why marketplaces cannot be used automatically";
  }
});

await check("plain http is refused", async () => {
  try {
    await openAssets.fetchOpenAsset("http://api.polyhaven.com/x.png", undefined, undefined, 1024 * 1024);
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/only https/i.test(err.message), `unhelpful: ${err.message}`);
    return "https required";
  }
});

await check("an unknown host needs an explicit licence claim", async () => {
  try {
    await openAssets.fetchOpenAsset("https://example.invalid/tex.png", undefined, undefined, 1024);
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/Pass licence/i.test(err.message), `unhelpful: ${err.message}`);
    return "licence claim required, so it is attributable";
  }
});

// ---------------------------------------------------------------- network path

let chosen = null;

await check("search finds CC0 assets", async () => {
  try {
    const result = await openAssets.searchPolyHaven("brick", "textures", 5);
    assert(result.licence === "CC0-1.0", `unexpected licence: ${result.licence}`);
    assert(result.matchCount > 0, "no matches for a common search term");
    chosen = result.matches[0].id;
    return `${result.matchCount} match(es), first = ${chosen}`;
  } catch (err) {
    if (offline(err)) return "SKIP no network access to api.polyhaven.com";
    throw err;
  }
});

let smallest = null;

await check("file listing is smallest-first", async () => {
  if (!chosen) return "SKIP search did not run";
  try {
    const files = await openAssets.polyHavenFiles(chosen);
    assert(files.optionCount > 0, "no downloadable files");
    const sized = files.options.filter((o) => typeof o.bytes === "number");
    for (let i = 1; i < sized.length; i++) {
      assert(sized[i].bytes >= sized[i - 1].bytes, "options are not sorted smallest-first");
    }
    smallest = sized.find((o) => o.bytes > 0 && o.bytes < 4 * 1024 * 1024) ?? sized[0];
    return `${files.optionCount} options; smallest usable = ${smallest.path} (${Math.round(smallest.bytes / 1024)}KB)`;
  } catch (err) {
    if (offline(err)) return "SKIP no network access";
    throw err;
  }
});

let fetched = null;

await check("fetch downloads with a checksum and does not touch the project", async () => {
  if (!smallest) return "SKIP file listing did not run";
  try {
    fetched = await openAssets.fetchOpenAsset(smallest.url, undefined, undefined, 8 * 1024 * 1024);
    assert(existsSync(fetched.file), `file missing at ${fetched.file}`);
    assert(statSync(fetched.file).size === fetched.bytes, "reported size does not match the file on disk");
    assert(fetched.sha256 && fetched.sha256.length === 64, `bad checksum: ${fetched.sha256}`);
    assert(fetched.licence === "CC0-1.0", `licence not carried: ${fetched.licence}`);
    assert(!fetched.file.includes("/Assets/"), "staged into the project instead of a staging directory");
    assert(/assets_import_file/.test(fetched.nextStep), "does not say how to import it");
    return `${Math.round(fetched.bytes / 1024)}KB from ${fetched.host}, sha256 ${fetched.sha256.slice(0, 12)}…`;
  } catch (err) {
    if (offline(err)) return "SKIP no network access";
    throw err;
  }
});

await check("import carries the provenance into the ledger", async () => {
  if (!fetched) return "SKIP nothing was fetched";
  const imported = await rpc("assets.import_file", {
    sourcePath: fetched.file,
    destFolder: "Assets/Agentic/OpenAssets",
    source: fetched.source,
    licence: fetched.licence,
    sourceUrl: fetched.url,
  }, { timeoutMs: 180_000 });
  assert(imported.ledgerEntry.licence === "CC0-1.0", "licence not recorded in the ledger");
  assert(imported.ledgerEntry.sourceUrl === fetched.url, "source URL not recorded");

  const ledger = await rpc("assets.ledger", { writeAttribution: true }, { timeoutMs: 120_000 });
  const entry = ledger.entries.find((e) => String(e.sourceUrl ?? "") === fetched.url);
  assert(entry, "the fetched asset is not in the ledger");
  return `imported ${imported.imported}; ledger now has ${ledger.entryCount} entr(y/ies)`;
});

console.log(`\n${passed}/${passed + failed} checks passed${skipped ? `, ${skipped} skipped` : ""}`);
process.exit(failed > 0 ? 1 : 0);
