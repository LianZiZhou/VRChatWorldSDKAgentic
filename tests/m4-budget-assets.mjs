#!/usr/bin/env node
/**
 * M4 acceptance test: dual-platform budget, asset organisation with provenance, and procedural geometry.
 *
 * The budget limits are asserted to come from the SDK rather than from constants in our code, because
 * VRChat owns those numbers and a stale copy would be worse than none.
 */
import { existsSync, mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";

const SCRATCH = process.env.TEMP ?? ".";
let passed = 0;
let failed = 0;

async function check(name, fn) {
  try {
    const detail = await fn();
    passed++;
    console.log(`PASS  ${name}`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 5).join("\n      ")}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function waitForIdle(budgetMs = 300_000) {
  const deadline = Date.now() + budgetMs;
  while (Date.now() < deadline) {
    try {
      const c = await rpc("unity.compile_status", {}, { timeoutMs: 20_000, reconnectWindowMs: budgetMs });
      if (!c.isCompiling && !c.isUpdatingAssets) return c;
    } catch {
      /* reloading */
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error("editor never went idle");
}

await check("editor idle and the budget/asset surface is registered", async () => {
  await waitForIdle();
  await rpc("bridge.harden_editor", { apply: true }, { timeoutMs: 60_000 });
  const methods = (await rpc("bridge.describe")).map((m) => m.name);
  for (const needed of [
    "budget.report", "budget.snapshot", "budget.diff", "budget.import_audit",
    "assets.inventory", "assets.unused", "assets.make_material", "assets.import_file", "assets.ledger",
    "geo.generate", "geo.kinds",
  ]) {
    assert(methods.includes(needed), `${needed} not registered`);
  }
  return `${methods.length} bridge methods`;
});

// ---------------------------------------------------------------- budget

await check("budget limits come from the SDK, not from our constants", async () => {
  const r = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
  assert(r.limits, "no limits block");
  assert(String(r.limits.source).includes("ValidationHelpers"),
    `limits are not sourced from the SDK: ${JSON.stringify(r.limits)}`);
  // The SDK enforces no size limit for PC worlds (it returns -1); "no limit" is a real answer, not a gap.
  assert(typeof r.limits.compressedEnforced === "boolean", `no enforcement flag: ${JSON.stringify(r.limits)}`);
  const android = await rpc("budget.report", { platform: "Android" }, { timeoutMs: 180_000 });
  assert(android.limits.compressedEnforced === true && android.limits.compressedBytes > 0,
    `mobile must have an enforced limit: ${JSON.stringify(android.limits)}`);
  return `PC enforced=${r.limits.compressedEnforced}, Android ${android.limits.compressedMegabytes.toFixed(0)}MB (${r.limits.source})`;
});

await check("budget report measures the scene", async () => {
  const r = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
  const s = r.scene;
  assert(typeof s.triangles === "number", "no triangle count");
  assert(s.renderers > 0, `no renderers counted: ${JSON.stringify(s).slice(0, 200)}`);
  assert(s.vrchatComponents, "no VRChat component counts");
  return `tris=${s.triangles} renderers=${s.renderers} materials=${s.uniqueMaterials} textures=${s.uniqueTextures} udon=${s.vrchatComponents.udonBehaviours}`;
});

await check("android profile yields different limits and its own findings", async () => {
  const pc = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
  const android = await rpc("budget.report", { platform: "Android" }, { timeoutMs: 180_000 });
  assert(android.mobileProfile === true, "android report is not using the mobile profile");
  assert(
    android.limits.compressedBytes !== pc.limits.compressedBytes ||
      android.findings.length !== pc.findings.length,
    "android and PC reports are indistinguishable"
  );
  const codes = android.findings.map((f) => f.code);
  return `android limit ${android.limits.compressedMegabytes.toFixed(0)}MB, findings=${JSON.stringify(codes)}`;
});

await check("bundle size is measured against the limit when a build exists", async () => {
  const r = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
  if (!r.bundle.found) return `no bundle yet (reported honestly): ${r.bundle.note}`;
  assert(r.bundle.withinLimit === true, `bundle is over the limit: ${JSON.stringify(r.bundle)}`);
  if (typeof r.bundle.percentOfLimit === "number")
    return `${r.bundle.megabytes.toFixed(2)}MB = ${r.bundle.percentOfLimit.toFixed(1)}% of the limit`;
  assert(r.bundle.limitNote, "no limit and no explanation of why");
  return `${r.bundle.megabytes.toFixed(2)}MB; ${r.bundle.limitNote}`;
});

await check("snapshot then diff shows the cost of a change", async () => {
  await rpc("budget.snapshot", { name: "m4-baseline", platform: "Windows" }, { timeoutMs: 180_000 });

  // Add geometry, then prove the diff notices.
  await rpc("geo.generate", {
    kind: "stairs",
    path: "BudgetProbe/Stairs",
    steps: 14,
    stepHeight: 0.18,
    stepDepth: 0.3,
    width: 1.6,
    saveScene: false,
  }, { timeoutMs: 180_000 });

  const diff = await rpc("budget.diff", { name: "m4-baseline", platform: "Windows" }, { timeoutMs: 180_000 });
  const renderers = diff.changes.find((c) => c.metric === "renderers");
  assert(renderers, `renderers did not change: ${JSON.stringify(diff.changes).slice(0, 300)}`);
  assert(renderers.delta >= 14, `expected at least 14 new renderers, got ${renderers.delta}`);

  await rpc("scene.apply_spec", { delete: ["BudgetProbe"], saveScene: true });
  return `${diff.changedMetricCount} metrics moved; renderers +${renderers.delta}`;
});

await check("import audit reports what does not match the platform profile", async () => {
  const r = await rpc("budget.import_audit", { platform: "Android" }, { timeoutMs: 300_000 });
  assert(r.applied === false, "audit must not change anything");
  assert(typeof r.assetsExamined === "number", "no examined count");
  assert(r.profile.textureFormat.startsWith("ASTC"), `Android profile should want ASTC, got ${r.profile.textureFormat}`);
  return `examined=${r.assetsExamined} textures=${r.textures.length} audio=${r.audio.length} models=${r.models.length} wants ${r.profile.textureFormat} @ ${r.profile.maxTextureSize}`;
});

// ---------------------------------------------------------------- assets

await check("inventory totals the project by extension", async () => {
  const r = await rpc("assets.inventory", {}, { timeoutMs: 180_000 });
  assert(r.assetCount > 0, "no assets found");
  assert(Array.isArray(r.byExtension) && r.byExtension.length > 0, "no extension breakdown");
  const top = r.byExtension[0];
  return `${r.assetCount} assets, ${r.totalMegabytes.toFixed(2)}MB, biggest bucket ${top.extension} (${top.megabytes.toFixed(2)}MB)`;
});

await check("unused reports reclaimable assets without condemning scripts", async () => {
  const r = await rpc("assets.unused", {}, { timeoutMs: 300_000 });
  assert(r.checked === true, `not checked: ${r.reason}`);
  const scripts = r.unused.filter((u) => u.path.endsWith(".cs"));
  assert(scripts.length === 0, `scripts must never be reported as unused: ${JSON.stringify(scripts.slice(0, 3))}`);
  return `${r.unusedCount} unused, ${r.reclaimableMegabytes.toFixed(2)}MB reclaimable across ${r.scenesConsidered.length} scene(s)`;
});

await check("make_material creates a material and reports unknown properties", async () => {
  const r = await rpc("assets.make_material", {
    path: "Assets/Agentic/Materials/ProbeMat.mat",
    shader: "Standard",
    colors: { _Color: [0.2, 0.6, 0.9, 1] },
    floats: { _Glossiness: 0.3, _NotARealProperty: 1 },
  }, { timeoutMs: 120_000 });
  assert(r.created === true || r.created === false, "no created flag");
  assert(r.assigned.some((x) => x.startsWith("_Color")), `colour not assigned: ${JSON.stringify(r.assigned)}`);
  assert(r.skipped.some((x) => x.includes("_NotARealProperty")), `bogus property not reported: ${JSON.stringify(r.skipped)}`);
  return `created=${r.created} assigned=${r.assigned.length} skipped=${JSON.stringify(r.skipped)}`;
});

await check("import_file refuses an asset with no recorded provenance", async () => {
  const dir = join(SCRATCH, "agentic-m4");
  mkdirSync(dir, { recursive: true });
  const file = join(dir, "probe.txt");
  writeFileSync(file, "synthetic asset for the M4 provenance test\n");

  try {
    await rpc("assets.import_file", { sourcePath: file, destFolder: "Assets/Agentic/Imported" }, { timeoutMs: 120_000 });
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/requires "source" and "licence"|cannot be cleared for distribution/.test(err.message), `unhelpful: ${err.message}`);
    return "refused without source and licence";
  }
});

await check("import_file records provenance and ledger generates ATTRIBUTION.md", async () => {
  const dir = join(SCRATCH, "agentic-m4");
  const file = join(dir, "probe.txt");

  const imported = await rpc("assets.import_file", {
    sourcePath: file,
    destFolder: "Assets/Agentic/Imported",
    name: "probe.txt",
    source: "synthetic test fixture",
    licence: "CC0-1.0",
    sourceUrl: "https://example.invalid/probe.txt",
  }, { timeoutMs: 120_000 });
  assert(imported.ledgerEntry.sha256, "no checksum recorded");
  assert(imported.ledgerEntry.licence === "CC0-1.0", "licence not recorded");

  const ledger = await rpc("assets.ledger", { writeAttribution: true }, { timeoutMs: 120_000 });
  assert(ledger.entryCount >= 1, "ledger is empty");
  assert(ledger.attributionFile, "no attribution file written");
  assert(existsSync(ledger.attributionFile), `attribution file missing at ${ledger.attributionFile}`);
  const text = readFileSync(ledger.attributionFile, "utf8");
  assert(text.includes("CC0-1.0"), "attribution does not mention the licence");
  assert(text.includes("probe.txt"), "attribution does not mention the asset");

  // Re-importing must replace the record, not duplicate it.
  await rpc("assets.import_file", {
    sourcePath: file, destFolder: "Assets/Agentic/Imported", name: "probe.txt",
    source: "synthetic test fixture", licence: "CC0-1.0",
  }, { timeoutMs: 120_000 });
  const again = await rpc("assets.ledger", {});
  const duplicates = again.entries.filter((e) => String(e.asset).endsWith("probe.txt"));
  assert(duplicates.length === 1, `ledger duplicated the entry ${duplicates.length} times`);

  return `ledger=${again.entryCount} entries, attribution at ${ledger.attributionFile}`;
});

// ---------------------------------------------------------------- geometry

await check("geo.kinds documents what it can build", async () => {
  const r = await rpc("geo.kinds");
  const kinds = r.kinds.map((k) => k.kind);
  assert(kinds.includes("room") && kinds.includes("corridor") && kinds.includes("stairs"), `thin: ${JSON.stringify(kinds)}`);
  return kinds.join(", ");
});

await check("geo.generate builds a room with a doorway cut into the wall", async () => {
  const r = await rpc("geo.generate", {
    kind: "room",
    path: "GeoProbe/Room",
    size: [10, 3, 8],
    thickness: 0.2,
    floor: true,
    ceiling: true,
    doors: [{ wall: "north", width: 1.6, height: 2.2, offset: 0 }],
    saveScene: true,
  }, { timeoutMs: 180_000 });

  const names = r.pieces.map((p) => p.name);
  assert(names.includes("Floor") && names.includes("Ceiling"), `missing floor/ceiling: ${JSON.stringify(names)}`);
  // The doorway must produce two side segments plus a lintel, not one solid wall.
  const northPieces = names.filter((n) => n.startsWith("north"));
  assert(northPieces.length >= 3, `doorway was not cut; north wall pieces: ${JSON.stringify(northPieces)}`);
  assert(northPieces.some((n) => n.includes("Lintel")), `no lintel above the door: ${JSON.stringify(northPieces)}`);
  // No piece may have a non-positive dimension: that is the silent hole-in-the-wall bug.
  for (const piece of r.pieces) {
    const s = piece.localScale;
    assert(s.x > 0 && s.y > 0 && s.z > 0, `degenerate piece ${piece.name}: ${JSON.stringify(s)}`);
  }
  return `${r.pieceCount} pieces, ~${r.approximateTriangles} tris, north wall = ${JSON.stringify(northPieces)}`;
});

await check("geo.generate is idempotent", async () => {
  const first = await rpc("geo.generate", {
    kind: "room", path: "GeoProbe/Room", size: [10, 3, 8],
    doors: [{ wall: "north", width: 1.6, height: 2.2 }], saveScene: false,
  }, { timeoutMs: 180_000 });
  assert(first.childrenReplaced > 0, "regeneration did not replace the previous children");
  const describe = await rpc("scene.describe", { root: "GeoProbe/Room", maxDepth: 1 });
  assert(describe.nodes[0].childCount === first.pieceCount,
    `expected ${first.pieceCount} children, found ${describe.nodes[0].childCount} — geometry is stacking up`);
  return `replaced ${first.childrenReplaced}, now ${describe.nodes[0].childCount} children`;
});

await check("geo.generate builds a corridor and stairs", async () => {
  const corridor = await rpc("geo.generate", {
    kind: "corridor", path: "GeoProbe/Hall", from: [0, 0, 4], to: [0, 0, 16], width: 2, height: 3, saveScene: false,
  }, { timeoutMs: 180_000 });
  assert(corridor.pieceCount >= 4, `corridor too thin: ${corridor.pieceCount} pieces`);

  const stairs = await rpc("geo.generate", {
    kind: "stairs", path: "GeoProbe/Stairs", steps: 12, stepHeight: 0.2, stepDepth: 0.3, width: 1.6, saveScene: true,
  }, { timeoutMs: 180_000 });
  assert(stairs.pieceCount === 12, `expected 12 steps, got ${stairs.pieceCount}`);
  // Each tread rises from the ground, so no step may float.
  for (const piece of stairs.pieces) {
    assert(Math.abs(piece.localPosition.y - piece.localScale.y / 2) < 0.001,
      `step ${piece.name} does not sit on the ground: pos.y=${piece.localPosition.y} height=${piece.localScale.y}`);
  }
  return `corridor=${corridor.pieceCount} pieces, stairs=${stairs.pieceCount} steps all grounded`;
});

await check("the generated room passes a spatial audit", async () => {
  const audit = await rpc("scene.spatial_audit", { gridStep: 1.5 }, { timeoutMs: 180_000 });
  const floatingRoom = audit.findings.filter((f) => f.code === "floating" && String(f.subject).includes("GeoProbe"));
  assert(floatingRoom.length === 0, `generated geometry reported as floating: ${JSON.stringify(floatingRoom.slice(0, 3))}`);
  const noCollider = audit.findings.filter((f) => f.code === "no-collider" && String(f.subject).includes("GeoProbe"));
  assert(noCollider.length === 0, `generated geometry lacks colliders: ${JSON.stringify(noCollider.slice(0, 3))}`);
  await rpc("scene.apply_spec", { delete: ["GeoProbe"], saveScene: true });
  return `coverage=${(audit.floorCoverage.coverage * 100).toFixed(0)}%, no floating or collider-less generated pieces`;
});

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
