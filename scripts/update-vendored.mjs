#!/usr/bin/env node
/**
 * Refreshes the vendored copy of MCP for Unity (CoplayDev, MIT) in third_party/unity-mcp.
 *
 *   node scripts/update-vendored.mjs --tag v10.3.0
 *   node scripts/update-vendored.mjs --tag v10.3.0 --dry-run
 *
 * Vendoring is what makes the generic Unity half work out of the box, and the price of that is having to
 * update it deliberately. This script is that deliberate step: it fetches one pinned tag, replaces the
 * tracked subset, and refuses to proceed if the new tag is not still under a licence that permits
 * redistribution — the whole arrangement depends on that, so it is checked rather than assumed.
 *
 * After it runs: re-run scripts/install.mjs (Unity must re-resolve the package), then the test suites. An
 * upstream release can rename tools, and our documentation names some of them.
 */
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(join(dirname(fileURLToPath(import.meta.url)), ".."));
const vendorDir = join(repo, "third_party", "unity-mcp");

const UPSTREAM = "CoplayDev/unity-mcp";

/** Exactly what we redistribute. Everything else upstream ships is development material. */
const KEEP = ["MCPForUnity", "Server", "CustomTools", "LICENSE", "README.md", "manifest.json"];

/** Preserved across an update: our own notes about the vendoring. */
const OURS = ["VENDORED.md"];

const flags = new Map();
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  if (!argv[i].startsWith("--")) continue;
  const name = argv[i].slice(2);
  const next = argv[i + 1];
  if (next && !next.startsWith("--")) {
    flags.set(name, next);
    i++;
  } else {
    flags.set(name, "true");
  }
}

const tag = flags.get("tag");
const dryRun = flags.get("dry-run") === "true";

if (!tag || flags.has("help")) {
  process.stdout.write(
    [
      "Usage: node scripts/update-vendored.mjs --tag <upstream tag> [--dry-run]",
      "",
      `  --tag <tag>   the ${UPSTREAM} tag to vendor, e.g. v10.3.0`,
      "  --dry-run     fetch and report what would change, without writing to third_party/",
      "",
    ].join("\n")
  );
  process.exit(tag ? 0 : 2);
}

function fail(message) {
  process.stderr.write(`update-vendored: ${message}\n`);
  process.exit(1);
}

// ---------------------------------------------------------------- fetch

const url = `https://codeload.github.com/${UPSTREAM}/tar.gz/refs/tags/${tag}`;
process.stdout.write(`fetching ${url}\n`);

const response = await fetch(url, { redirect: "follow" });
if (!response.ok) {
  fail(`${url} returned ${response.status} ${response.statusText}. Check that the tag exists.`);
}
const tarball = Buffer.from(await response.arrayBuffer());
process.stdout.write(`fetched ${Math.round(tarball.length / 1024)}KB\n`);

const work = mkdtempSync(join(tmpdir(), "vrcw-vendor-"));
const archive = join(work, "upstream.tar.gz");
writeFileSync(archive, tarball);

// bsdtar ships with Windows 10 and later, and tar is everywhere else. Node has no archive reader of its own.
//
// Run inside the temp directory with a bare filename: GNU tar reads "C:\path" as a remote host
// specification and tries to connect to a machine called C, which fails in a thoroughly confusing way.
const extracted = spawnSync("tar", ["-xzf", "upstream.tar.gz"], { cwd: work, encoding: "utf8", timeout: 300_000 });
if (extracted.error || extracted.status !== 0) {
  fail(`could not extract the tarball: ${extracted.error?.message ?? extracted.stderr ?? extracted.status}`);
}

const roots = readdirSync(work).filter((entry) => entry !== "upstream.tar.gz");
if (roots.length !== 1) fail(`expected one directory inside the tarball, found: ${roots.join(", ") || "none"}`);
const source = join(work, roots[0]);

// ---------------------------------------------------------------- licence gate

{
  const licenceFile = join(source, "LICENSE");
  if (!existsSync(licenceFile)) {
    fail(`${tag} has no LICENSE file. Redistribution here depends on it; refusing to vendor.`);
  }
  const licence = readFileSync(licenceFile, "utf8");
  const permissive = /MIT License/i.test(licence) && /Permission is hereby granted, free of charge/i.test(licence);
  if (!permissive) {
    fail(
      `${tag} does not carry the MIT licence this vendoring relies on. First 400 characters:\n` +
        licence.slice(0, 400)
    );
  }
  process.stdout.write("licence: MIT, and it still permits redistribution\n");
}

// ---------------------------------------------------------------- what would change

const missing = KEEP.filter((entry) => !existsSync(join(source, entry)));
if (missing.length) {
  fail(`${tag} does not contain: ${missing.join(", ")}. The upstream layout has moved; update KEEP in this script.`);
}

const upstreamVersion = (() => {
  try {
    return JSON.parse(readFileSync(join(source, "MCPForUnity", "package.json"), "utf8")).version ?? "unknown";
  } catch {
    return "unknown";
  }
})();

const currentVersion = (() => {
  try {
    return JSON.parse(readFileSync(join(vendorDir, "MCPForUnity", "package.json"), "utf8")).version ?? "unknown";
  } catch {
    return "not vendored yet";
  }
})();

process.stdout.write(`com.coplaydev.unity-mcp: ${currentVersion} -> ${upstreamVersion} (tag ${tag})\n`);

if (dryRun) {
  process.stdout.write(`dry run: ${vendorDir} untouched. Would replace: ${KEEP.join(", ")}\n`);
  rmSync(work, { recursive: true, force: true });
  process.exit(0);
}

// ---------------------------------------------------------------- replace

mkdirSync(vendorDir, { recursive: true });

for (const entry of KEEP) {
  const target = join(vendorDir, entry);
  rmSync(target, { recursive: true, force: true });
  cpSync(join(source, entry), target, { recursive: true });
  process.stdout.write(`  replaced ${entry}\n`);
}

for (const entry of OURS) {
  if (!existsSync(join(vendorDir, entry))) {
    process.stdout.write(`  note: ${entry} is missing; the vendoring notes should be restored by hand\n`);
  }
}

// Keep the pinned-tag line in VENDORED.md honest — it is the only record of what this tree came from.
{
  const notes = join(vendorDir, "VENDORED.md");
  if (existsSync(notes)) {
    const before = readFileSync(notes, "utf8");
    const after = before.replace(
      /\| Pinned at \| tag \*\*[^*]+\*\*[^|]*\|/,
      `| Pinned at | tag **${tag}** (Unity package \`com.coplaydev.unity-mcp\` ${upstreamVersion}) |`
    );
    if (after !== before) {
      writeFileSync(notes, after, "utf8");
      process.stdout.write("  updated the pinned tag in VENDORED.md\n");
    } else {
      process.stdout.write("  could not find the pinned-tag row in VENDORED.md; update it by hand\n");
    }
  }
}

rmSync(work, { recursive: true, force: true });

process.stdout.write(
  [
    "",
    "Vendored. Next:",
    "  1. node scripts/install.mjs --project <your project>   (Unity has to re-resolve the package)",
    "  2. node tests/companion-mcp.mjs                        (handshake, telemetry, editor reachability)",
    "  3. node tests/m0-smoke.mjs && node tests/e2e-world.mjs  (nothing of ours regressed)",
    "  4. check that docs naming upstream tools are still accurate",
    "",
  ].join("\n")
);
