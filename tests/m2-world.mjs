#!/usr/bin/env node
/**
 * M2 acceptance test: project setup, world scaffolding, SDK validation, declarative scene building,
 * geometry auditing and visual survey — all without a human in the editor.
 *
 * Scaffolds into its own scene so the fixtures from other suites are left alone, and reopens the
 * original scene at the end.
 */
import { existsSync, statSync } from "node:fs";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";

const SCENE = "Assets/Scenes/AgenticScaffold.unity";
let originalScene = null;

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

// Remove the editor's interactive prompts before touching assets. A modal dialog halts Unity's update
// loop and therefore the bridge's pump, which would stall the whole suite with nobody to click the button.
await check("editor prompts are disabled", async () => {
  const h = await rpc("bridge.harden_editor", { apply: true }, { timeoutMs: 60_000 });
  const bad = (h.settings ?? []).filter((s) => !s.compliant).map((s) => s.key);
  assert(bad.length === 0, `still interactive: ${bad.join(", ")}`);
  return `changed=${JSON.stringify(h.changed)}`;
});


// ---------------------------------------------------------------- project

await check("editor idle, world surface registered", async () => {
  await waitForIdle();
  const methods = await rpc("bridge.describe");
  const names = methods.map((m) => m.name);
  for (const needed of ["world.scaffold", "world.validate", "scene.apply_spec", "scene.spatial_audit", "scene.survey_shots"]) {
    assert(names.includes(needed), `${needed} not registered`);
  }
  const state = await rpc("unity.editor_state");
  originalScene = state.scenes?.[0]?.path ?? null;
  return `${names.length} methods, current scene ${originalScene}`;
});

await check("fix_project sets up VRChat layers and the collision matrix", async () => {
  const before = await rpc("world.project_status");
  const after = await rpc("world.fix_project", {}, { timeoutMs: 120_000 });
  assert(after.layersSetUp === true, "layers still not set up");
  assert(after.collisionMatrixSetUp === true, "collision matrix still not set up");
  assert(after.buildWouldBeBlockedByLayers === false, "build would still be blocked by layers");
  return `before(layers=${before.layersSetUp}, matrix=${before.collisionMatrixSetUp}) applied=${JSON.stringify(after.applied)}`;
});

await check("fix_project is idempotent", async () => {
  const again = await rpc("world.fix_project", {}, { timeoutMs: 120_000 });
  // playerSettings is unconditional; layers/matrix must not be re-applied once correct.
  assert(!again.applied.includes("layers"), "re-applied layers unnecessarily");
  assert(!again.applied.includes("collisionMatrix"), "re-applied collision matrix unnecessarily");
  return `applied=${JSON.stringify(again.applied)}`;
});

// ---------------------------------------------------------------- scaffold

await check("scaffold refuses to discard unsaved work by default", async () => {
  await rpc("unity.eval_csharp", {
    code: `var go = new GameObject("__dirty_marker__"); EditorSceneManager.MarkSceneDirty(go.scene); return go.scene.isDirty;`,
  }, { timeoutMs: 120_000 });
  try {
    await rpc("world.scaffold", { path: SCENE, overwrite: true }, { timeoutMs: 180_000 });
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/unsaved changes/.test(err.message), `unhelpful: ${err.message}`);
    return "refused, naming the dirty scenes";
  } finally {
    await rpc("unity.eval_csharp", {
      code: `var go = GameObject.Find("__dirty_marker__"); if (go != null) UnityEngine.Object.DestroyImmediate(go);
EditorSceneManager.SaveOpenScenes(); return true;`,
    }, { timeoutMs: 120_000 });
  }
});

await check("scaffold creates a scene that satisfies the SDK's hard requirements", async () => {
  const r = await rpc("world.scaffold", { path: SCENE, addFloor: true, overwrite: true }, { timeoutMs: 180_000 });
  assert(r.saved === true, "scene was not saved");
  assert(r.descriptorCount === 1, `expected exactly 1 descriptor, got ${r.descriptorCount}`);
  const d = r.descriptors[0];
  assert(d.validSpawnCount >= 1, "no valid spawn point — the SDK would refuse to build");
  assert(d.hasPipelineManagerOnSameObject === true, "PipelineManager not on the descriptor's GameObject");
  assert(r.problems.length === 0, `problems reported: ${JSON.stringify(r.problems)}`);
  return `spawns=${d.validSpawnCount} refCamera=${r.referenceCameraAssigned} respawnY=${d.respawnHeightY} problems=0`;
});

await check("scaffold refuses to overwrite without permission", async () => {
  try {
    await rpc("world.scaffold", { path: SCENE }, { timeoutMs: 180_000 });
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/already exists/.test(err.message), `unhelpful: ${err.message}`);
    return "refused without overwrite:true";
  }
});

// ---------------------------------------------------------------- validation

await check("validate returns SDK validations as data", async () => {
  const r = await rpc("world.validate", {}, { timeoutMs: 180_000 });
  if (!r.ready) {
    // Honest outcome, not a pass-by-default: validation genuinely needs the one-time sign-in.
    assert(/signed in|control panel|builder/i.test(r.reason), `unexpected reason: ${r.reason}`);
    return `not ready (expected without sign-in): ${r.reason}`;
  }
  assert(Array.isArray(r.errors), "no errors array");
  assert(Array.isArray(r.warnings), "no warnings array");
  return `builderValid=${r.builderValid} errors=${r.errorCount} warnings=${r.warnings.length} stats=${r.stats.length}`;
});

// ---------------------------------------------------------------- apply_spec

const ROOM_SPEC = {
  objects: [
    { path: "Room", position: [0, 0, 0] },
    { path: "Room/FloorTile", primitive: "Cube", position: [0, -0.05, 0], scale: [12, 0.1, 12] },
    { path: "Room/WallNorth", primitive: "Cube", position: [0, 1.5, 6], scale: [12, 3, 0.2] },
    { path: "Room/WallSouth", primitive: "Cube", position: [0, 1.5, -6], scale: [12, 3, 0.2] },
    { path: "Room/WallEast", primitive: "Cube", position: [6, 1.5, 0], scale: [0.2, 3, 12] },
    { path: "Room/WallWest", primitive: "Cube", position: [-6, 1.5, 0], scale: [0.2, 3, 12] },
    {
      path: "Room/TriggerZone",
      position: [0, 1, 3],
      components: [{ type: "BoxCollider", fields: { isTrigger: true, size: [2, 2, 2] } }],
    },
    { path: "Room/Lamp", components: [{ type: "Light", fields: { type: "Point", intensity: 2.5, range: 12 } }], position: [0, 2.5, 0] },
  ],
};

await check("apply_spec builds a room in one call", async () => {
  const r = await rpc("scene.apply_spec", { ...ROOM_SPEC, saveScene: true }, { timeoutMs: 180_000 });
  assert(r.created.length === 8, `expected 8 created, got ${r.created.length}: ${JSON.stringify(r.created)}`);
  assert(r.componentsAdded.length >= 2, `expected components added, got ${JSON.stringify(r.componentsAdded)}`);
  return `created=${r.created.length} components=${r.componentsAdded.length} warnings=${JSON.stringify(r.warnings)}`;
});

await check("apply_spec is idempotent", async () => {
  const r = await rpc("scene.apply_spec", { ...ROOM_SPEC, saveScene: true }, { timeoutMs: 180_000 });
  assert(r.created.length === 0, `re-apply created duplicates: ${JSON.stringify(r.created)}`);
  assert(r.updated.length === 8, `expected 8 updated, got ${r.updated.length}`);
  assert(r.componentsAdded.length === 0, `re-added components: ${JSON.stringify(r.componentsAdded)}`);
  return `created=0 updated=${r.updated.length}`;
});

await check("apply_spec dryRun changes nothing", async () => {
  const r = await rpc("scene.apply_spec", {
    objects: [{ path: "Room/NotReal", primitive: "Sphere" }],
    dryRun: true,
  });
  assert(r.dryRun === true, "dryRun not reported");
  assert(r.created.includes("Room/NotReal"), "dryRun did not predict the creation");
  const after = await rpc("scene.describe", { root: "Room", maxDepth: 1 });
  const names = (after.nodes[0].children ?? []).map((c) => c.name);
  assert(!names.includes("NotReal"), "dryRun actually created the object");
  return `predicted ${JSON.stringify(r.created)}, scene unchanged`;
});

await check("apply_spec warns about components VRChat strips at build", async () => {
  // Chosen from the live whitelist rather than hardcoded: which UnityEngine components VRChat permits
  // changes between SDK versions, and a component with an unsatisfied [RequireComponent] would fail for
  // an unrelated reason.
  const whitelist = await rpc("scene.component_whitelist", {});
  const allowed = new Set(whitelist.types);
  // VRChat's whitelist is broad (311 entries), so these were found by enumerating every public
  // Component with no [RequireComponent] and diffing against the live whitelist.
  const candidates = [
    "UnityEngine.Animation",
    "UnityEngine.ArticulationBody",
    "UnityEngine.UIElements.UIDocument",
    "UnityEngine.SpatialTracking.TrackedPoseDriver",
  ];
  const victim = candidates.find((c) => !allowed.has(c));
  assert(victim, `every candidate is whitelisted; pick another: ${candidates.join(", ")}`);

  const r = await rpc("scene.apply_spec", {
    objects: [{ path: "Room/StrippedProbe", components: [{ type: victim }] }],
    saveScene: false,
  });
  const stripped = r.warnings.find((w) => /whitelist/.test(w));
  assert(stripped, `expected a whitelist warning for ${victim}, got ${JSON.stringify(r.warnings)}`);
  await rpc("scene.apply_spec", { delete: ["Room/StrippedProbe"], saveScene: true });
  return `${victim}: ${stripped.slice(0, 120)}`;
});

await check("apply_spec refuses to add an UdonSharpBehaviour the wrong way", async () => {
  try {
    await rpc("scene.apply_spec", {
      objects: [{ path: "Room/BadUdon", components: [{ type: "AgenticTestDoor" }] }],
    });
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/udon\.attach/.test(err.message), `unhelpful: ${err.message}`);
    return "pointed at udon.attach instead";
  }
});

await check("apply_spec deletes", async () => {
  await rpc("scene.apply_spec", { objects: [{ path: "Room/Temp", primitive: "Sphere" }], saveScene: false });
  const r = await rpc("scene.apply_spec", { delete: ["Room/Temp"], saveScene: true });
  assert(r.deleted.includes("Room/Temp"), `not deleted: ${JSON.stringify(r.deleted)}`);
  return "deleted Room/Temp";
});

await check("component_whitelist is readable", async () => {
  const all = await rpc("scene.component_whitelist", {});
  assert(all.total > 50, `whitelist looks wrong: ${all.total} entries`);
  const vrc = await rpc("scene.component_whitelist", { filter: "VRC.SDK3.Components" });
  assert(vrc.matched > 5, `expected VRChat components, got ${vrc.matched}`);
  return `${all.total} total, ${vrc.matched} under VRC.SDK3.Components`;
});

// ---------------------------------------------------------------- audit

await check("spatial_audit grounds the spawn and measures floor coverage", async () => {
  const r = await rpc("scene.spatial_audit", { gridStep: 1.5 }, { timeoutMs: 180_000 });
  assert(r.spawns.length >= 1, "no spawns audited");
  const spawn = r.spawns[0];
  assert(spawn.grounded === true, `spawn is not grounded: ${JSON.stringify(spawn)}`);
  assert(r.floorCoverage.checked === true, "floor coverage not computed");
  assert(r.floorCoverage.coverage > 0.5, `coverage only ${r.floorCoverage.coverage}`);
  return `grounded at ${spawn.groundDistance.toFixed(2)}m on ${spawn.groundObject}, coverage=${(r.floorCoverage.coverage * 100).toFixed(0)}%`;
});

await check("spatial_audit catches a floating object with no collider", async () => {
  await rpc("scene.apply_spec", {
    objects: [{ path: "Room/FloatingOrb", primitive: "Sphere", position: [0, 40, 0], scale: [3, 3, 3] }],
    saveScene: false,
  });
  // A primitive sphere brings its own collider, so strip it to also exercise the collider check.
  await rpc("unity.eval_csharp", {
    code: `var go = GameObject.Find("Room/FloatingOrb");
var c = go.GetComponent<Collider>(); if (c != null) UnityEngine.Object.DestroyImmediate(c);
return go.GetComponent<Collider>() == null;`,
  }, { timeoutMs: 120_000 });

  const r = await rpc("scene.spatial_audit", { gridStep: 2 }, { timeoutMs: 180_000 });
  const floating = r.findings.find((f) => f.code === "floating" && String(f.subject).includes("FloatingOrb"));
  const noCollider = r.findings.find((f) => f.code === "no-collider" && String(f.subject).includes("FloatingOrb"));
  assert(floating, `floating not detected: ${JSON.stringify(r.findings.map((f) => f.code))}`);
  assert(noCollider, `missing collider not detected: ${JSON.stringify(r.findings.map((f) => f.code))}`);
  await rpc("scene.apply_spec", { delete: ["Room/FloatingOrb"], saveScene: true });
  return `${floating.code}: ${floating.message.slice(0, 90)} | ${noCollider.code}`;
});

await check("spatial_audit catches interpenetration", async () => {
  await rpc("scene.apply_spec", {
    objects: [
      { path: "Room/BuriedA", primitive: "Cube", position: [2, 1, 2], scale: [2, 2, 2] },
      { path: "Room/BuriedB", primitive: "Cube", position: [2.1, 1, 2.1], scale: [2, 2, 2] },
    ],
    saveScene: false,
  });
  const r = await rpc("scene.spatial_audit", { gridStep: 3 }, { timeoutMs: 180_000 });
  const overlap = r.findings.find((f) => f.code === "interpenetrating" && String(f.subject).includes("Buried"));
  assert(overlap, `not detected: ${JSON.stringify(r.findings.map((f) => f.code))}`);
  await rpc("scene.apply_spec", { delete: ["Room/BuriedA", "Room/BuriedB"], saveScene: true });
  return overlap.message.slice(0, 110);
});

// ---------------------------------------------------------------- survey

await check("survey_shots renders real PNGs from the spawn and an orbit", async () => {
  const r = await rpc("scene.survey_shots", { mode: "all", width: 640, height: 360, yawSteps: 2, orbitCount: 2 }, { timeoutMs: 180_000 });
  assert(r.shotCount >= 4, `expected several shots, got ${r.shotCount}`);

  let smallest = Infinity;
  for (const shot of r.shots) {
    assert(existsSync(shot.file), `missing file ${shot.file}`);
    const size = statSync(shot.file).size;
    smallest = Math.min(smallest, size);
  }
  // A blank or failed render produces a tiny PNG; a real one does not.
  assert(smallest > 2000, `smallest PNG is only ${smallest} bytes — renders are probably blank`);

  const names = r.shots.map((s) => s.name);
  assert(names.some((n) => n.startsWith("spawn")), `no spawn view: ${JSON.stringify(names)}`);
  assert(names.some((n) => n.startsWith("orbit")), `no orbit view: ${JSON.stringify(names)}`);
  assert(names.includes("top"), `no top view: ${JSON.stringify(names)}`);
  return `${r.shotCount} shots, smallest ${Math.round(smallest / 1024)}KB, dir ${r.directory}`;
});

// ---------------------------------------------------------------- restore

await check("original scene reopened", async () => {
  if (!originalScene) return "no original scene recorded; left the scaffold open";
  const r = await rpc("unity.eval_csharp", {
    code: `EditorSceneManager.SaveOpenScenes();
var s = EditorSceneManager.OpenScene("${originalScene}", OpenSceneMode.Single);
return s.path;`,
  }, { timeoutMs: 180_000 });
  assert(r.compiled && !r.threw, `reopen failed: ${r.message ?? JSON.stringify(r.diagnostics?.slice(0, 2))}`);
  return `reopened ${r.value}`;
});

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
