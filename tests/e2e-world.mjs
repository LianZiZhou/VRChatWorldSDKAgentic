#!/usr/bin/env node
/**
 * End-to-end: build and verify a small interactive VRChat world using only the tool surface.
 *
 * This is the acceptance criterion for the whole toolchain. Nothing here touches Unity by hand — every step
 * is a call an agent could make — and it runs the full pipeline in the order the SDK actually requires:
 *
 *   checkpoint -> harden editor -> fix project -> scaffold scene -> generate architecture
 *   -> lint (no Unity) -> write script -> compile -> attach -> set fields
 *   -> audit geometry -> photograph -> budget both platforms
 *   -> verify behaviour in ClientSim -> build a real .vrcw -> checkpoint
 */
import { existsSync, statSync } from "node:fs";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";
import * as projectGit from "../packages/vrcw-mcp/dist/project.js";
import { spawnSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { writeFileSync, mkdirSync } from "node:fs";

const here = dirname(fileURLToPath(import.meta.url));
const PROJECT = process.env.VRCW_PROJECT ?? "E:/Unity/WorldTest";
const SCENE = "Assets/Scenes/AgenticE2E.unity";
const CLASS = "AgenticE2ECounter";
const ROOM = "World/Room";
const BUTTON = "World/Button";
const DISPLAY = "World/Display";

const analyzer = join(here, "..", "packages", "udon-analyzer", "bin", "Release", "net8.0",
  process.platform === "win32" ? "udon-analyzer.exe" : "udon-analyzer");

let passed = 0;
let failed = 0;
const timeline = [];

async function step(name, fn) {
  const started = Date.now();
  try {
    const detail = await fn();
    passed++;
    const ms = Date.now() - started;
    timeline.push({ name, ms });
    console.log(`PASS  ${name}  (${(ms / 1000).toFixed(1)}s)`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 6).join("\n      ")}`);
    throw err; // the pipeline is sequential; a failed step invalidates what follows
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

async function runJob(kind, params, waitMs = 900_000) {
  const job = await rpc("job.submit", { kind, params });
  const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
  const deadline = Date.now() + waitMs;
  let last = job;
  while (Date.now() < deadline) {
    try {
      last = await rpc("job.status", { id: job.id }, { timeoutMs: 20_000 });
      if (terminal.has(last.state)) return last;
    } catch {
      /* builds and play mode occupy the main thread */
    }
    await new Promise((r) => setTimeout(r, 1200));
  }
  throw new Error(`job ${kind} never finished: ${JSON.stringify(last).slice(0, 200)}`);
}

const SOURCE = `using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ${CLASS} : UdonSharpBehaviour
{
    public GameObject display;
    public int step;

    [UdonSynced] public int count;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        count = count + step;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        // Telemetry for real-client verification: Udon cannot send data out, so a tagged log line is the
        // only channel verify_multiclient can read back.
        Debug.Log("[AGENTTEL]{\\"counter\\":" + count + "}");
        if (display != null) display.SetActive(count > 0);
    }
}
`;

try {
  // ------------------------------------------------------------------ prepare

  await step("checkpoint the project before changing anything", async () => {
    const result = projectGit.checkpoint(PROJECT, "agentic e2e: before building the world", true);
    return result.committed ? `committed at ${result.head}` : `clean at ${result.head}`;
  });

  await step("editor is reachable, idle and non-interactive", async () => {
    await waitForIdle();
    const hardened = await rpc("bridge.harden_editor", { apply: true, disableDomainReloadOnPlay: true }, { timeoutMs: 60_000 });
    const bad = (hardened.settings ?? []).filter((s) => !s.compliant).map((s) => s.key);
    assert(bad.length === 0, `editor still interactive: ${bad.join(", ")}`);
    const health = await rpc("bridge.health");
    return `${health.methodCount} bridge methods, ${health.jobKinds.length} job kinds`;
  });

  await step("project is configured for VRChat", async () => {
    const fixed = await rpc("world.fix_project", {}, { timeoutMs: 120_000 });
    assert(fixed.buildWouldBeBlockedByLayers === false, "layers or collision matrix still wrong");
    return `layers=${fixed.layersSetUp} matrix=${fixed.collisionMatrixSetUp}`;
  });

  // ------------------------------------------------------------------ build the world

  await step("scaffold a world scene that satisfies the SDK", async () => {
    const world = await rpc("world.scaffold",
      { path: SCENE, addFloor: true, overwrite: true, discardUnsaved: true }, { timeoutMs: 180_000 });
    assert(world.problems.length === 0, `problems: ${JSON.stringify(world.problems)}`);
    assert(world.descriptors[0].validSpawnCount >= 1, "no valid spawn");
    return `${SCENE}: 1 descriptor, ${world.descriptors[0].validSpawnCount} spawn, floor=${world.createdFloor}`;
  });

  await step("generate a room with a doorway", async () => {
    const room = await rpc("geo.generate", {
      kind: "room", path: ROOM, size: [10, 3, 10], thickness: 0.2, floor: true, ceiling: true,
      doors: [{ wall: "north", width: 1.8, height: 2.2, offset: 0 }], saveScene: true,
    }, { timeoutMs: 180_000 });
    for (const piece of room.pieces) {
      const s = piece.localScale;
      assert(s.x > 0 && s.y > 0 && s.z > 0, `degenerate piece ${piece.name}`);
    }
    return `${room.pieceCount} pieces, ~${room.approximateTriangles} tris`;
  });

  await step("place the interactive objects declaratively", async () => {
    const applied = await rpc("scene.apply_spec", {
      objects: [
        { path: BUTTON, primitive: "Cube", position: [0, 1.2, 3], scale: [0.4, 0.4, 0.15] },
        { path: DISPLAY, primitive: "Sphere", position: [0, 2.2, 3], scale: [0.5, 0.5, 0.5], active: false },
      ],
      saveScene: true,
    }, { timeoutMs: 180_000 });
    assert(applied.warnings.length === 0, `warnings: ${JSON.stringify(applied.warnings)}`);
    return `created ${applied.created.length}, updated ${applied.updated.length}`;
  });

  // ------------------------------------------------------------------ the script

  await step("lint the script before Unity ever sees it", async () => {
    mkdirSync(join(here, "fixtures"), { recursive: true });
    const staged = join(here, "fixtures", `${CLASS}.cs`);
    writeFileSync(staged, SOURCE);

    const result = spawnSync(analyzer, ["--json", staged], { encoding: "utf8", timeout: 120_000 });
    const report = JSON.parse(result.stdout);
    assert(report.errorCount === 0, `linter rejected the script: ${JSON.stringify(report.diagnostics)}`);
    // Milliseconds, versus the tens of seconds a Unity round trip costs.
    return `clean in the linter (${report.warningCount} warning(s)), no Unity round trip spent`;
  });

  await step("write the script and let Unity compile it", async () => {
    const first = await rpc("udon.create_script",
      { className: CLASS, folder: "Assets/Udon", code: SOURCE }, { timeoutMs: 120_000 });
    await rpc("unity.refresh_assets", { saveAssets: true, forceSynchronous: true }, { timeoutMs: 180_000 }).catch(() => {});
    const compiled = await waitForIdle();
    assert(compiled.errorCount === 0, `C# errors: ${JSON.stringify(compiled.diagnostics?.slice(0, 3))}`);

    // Second pass creates the program asset, which can only be linked once the type exists.
    const second = await rpc("udon.create_script",
      { className: CLASS, folder: "Assets/Udon", code: SOURCE }, { timeoutMs: 120_000 });
    assert(second.programAssetExists, "program asset was not created");
    return `wrote ${first.scriptPath}, program asset ${second.programAssetExists ? "ready" : "missing"}`;
  });

  await step("compile the Udon program", async () => {
    let job = await runJob("udon.compile", {});
    if (job.state === "interrupted" || job.result?.stage === "upgrade") {
      await waitForIdle();
      job = await runJob("udon.compile", {});
    }
    assert(job.result?.ok === true, `compile failed: ${JSON.stringify(job.result).slice(0, 300)}`);
    return `stage=${job.result.stage} errors=${job.result.errorCount}`;
  });

  await step("attach the behaviour and wire its fields", async () => {
    const attached = await rpc("udon.attach", { gameObject: BUTTON, type: CLASS }, { timeoutMs: 60_000 });
    assert(attached.programAssetLinked, "program asset not linked");

    const set = await rpc("udon.set_fields", {
      gameObject: BUTTON, type: CLASS,
      fields: { step: 1, count: 0, display: { $gameObject: DISPLAY } },
      saveScene: true,
    }, { timeoutMs: 60_000 });
    assert(set.copiedToUdon, `CopyProxyToUdon failed: ${set.copyError}`);

    // The assertion that matters: the values reached the UdonBehaviour, not just the proxy.
    const read = await rpc("udon.read_vars", { gameObject: BUTTON, type: CLASS });
    assert(read.mismatchCount === 0, `proxy and Udon disagree: ${JSON.stringify(read.variables)}`);
    return `fields on Udon: ${read.variables.filter((v) => v.presentOnUdonBehaviour).map((v) => v.name).join(", ")}`;
  });

  // ------------------------------------------------------------------ inspect

  await step("geometry audit is clean", async () => {
    const audit = await rpc("scene.spatial_audit", { gridStep: 1.5 }, { timeoutMs: 180_000 });
    assert(audit.spawns[0]?.grounded === true, `spawn not grounded: ${JSON.stringify(audit.spawns[0])}`);
    assert(audit.errorCount === 0, `errors: ${JSON.stringify(audit.findings.filter((f) => f.severity === "error"))}`);
    return `spawn grounded at ${audit.spawns[0].groundDistance.toFixed(2)}m, coverage ${(audit.floorCoverage.coverage * 100).toFixed(0)}%, ${audit.warningCount} warning(s)`;
  });

  await step("photograph the world from the spawn", async () => {
    const shots = await rpc("scene.survey_shots",
      { mode: "all", width: 800, height: 450, yawSteps: 2, orbitCount: 2, label: "e2e" }, { timeoutMs: 180_000 });
    let smallest = Infinity;
    for (const shot of shots.shots) {
      assert(existsSync(shot.file), `missing ${shot.file}`);
      smallest = Math.min(smallest, statSync(shot.file).size);
    }
    assert(smallest > 2000, `smallest render is ${smallest} bytes, so the views are probably blank`);
    return `${shots.shotCount} renders, smallest ${Math.round(smallest / 1024)}KB, in ${shots.directory}`;
  });

  await step("budget is within limits on both platforms", async () => {
    const pc = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
    const android = await rpc("budget.report", { platform: "Android" }, { timeoutMs: 180_000 });
    assert(pc.errorCount === 0, `PC budget errors: ${JSON.stringify(pc.findings.filter((f) => f.severity === "error"))}`);
    assert(android.errorCount === 0, `Android budget errors: ${JSON.stringify(android.findings.filter((f) => f.severity === "error"))}`);
    return `tris=${pc.scene.triangles} materials=${pc.scene.uniqueMaterials} | PC ${pc.warningCount} warn, Android ${android.warningCount} warn`;
  });

  // ------------------------------------------------------------------ verify behaviour

  await step("ClientSim proves the counter works", async () => {
    const job = await runJob("verify.clientsim", {
      name: "counter increments and reveals the display",
      steps: [
        { wait_frames: 20 },
        { assert_var: { gameObject: BUTTON, type: CLASS, name: "count", equals: 0 } },
        { assert_active: { gameObject: DISPLAY, equals: false } },
        { interact: { gameObject: BUTTON, type: CLASS } },
        { wait_frames: 3 },
        { assert_var: { gameObject: BUTTON, type: CLASS, name: "count", equals: 1 } },
        { assert_active: { gameObject: DISPLAY, equals: true } },
        { interact: { gameObject: BUTTON, type: CLASS } },
        { wait_frames: 3 },
        { assert_var: { gameObject: BUTTON, type: CLASS, name: "count", equals: 2 } },
        { assert_no_errors: true },
      ],
    });
    assert(job.result?.ok === true,
      `scenario failed: ${JSON.stringify((job.result?.steps ?? []).filter((s) => !s.ok)).slice(0, 400)}`);
    const ignored = (job.result.steps ?? []).find((s) => s.step === "assert_no_errors")?.ignoredCount ?? 0;
    return `${job.result.stepCount} steps, 0 failures (${ignored} known SDK noise entr(y/ies) reported separately)`;
  });

  await step("the telemetry contract produced parseable output", async () => {
    // The world logs [AGENTTEL] lines; in ClientSim they land in the Unity console rather than a client log,
    // which is the same contract verify_multiclient reads from a real client.
    const console_ = await rpc("unity.console", { limit: 200, grep: "[AGENTTEL]" });
    assert(console_.returned > 0, "no telemetry lines were captured");
    const last = console_.entries[console_.entries.length - 1];
    const payload = JSON.parse(String(last.msg).slice(String(last.msg).indexOf("{")));
    assert(typeof payload.counter === "number", `unparseable telemetry: ${last.msg}`);
    return `${console_.returned} telemetry line(s), last counter=${payload.counter}`;
  });

  // ------------------------------------------------------------------ build

  await step("build a real .vrcw world bundle", async () => {
    const job = await runJob("verify.build_local", {}, 1_800_000);
    assert(job.result?.ok === true,
      `build failed at ${job.result?.stage}: ${job.result?.message} ${JSON.stringify(job.result?.validationErrors ?? "")}`);
    assert(job.result.bundleBytes > 0, "bundle is empty");
    return `${job.result.platform} ${job.result.bundleMegabytes.toFixed(2)}MB in ${job.result.elapsedSeconds.toFixed(1)}s`;
  });

  await step("the built bundle is measured against VRChat's limit", async () => {
    const report = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
    assert(report.bundle.found === true, "the build was not found by the budget report");
    assert(report.bundle.withinLimit === true, `over limit: ${JSON.stringify(report.bundle)}`);
    return `${report.bundle.megabytes.toFixed(2)}MB; ${report.bundle.limitNote ?? `${report.bundle.percentOfLimit.toFixed(1)}% of the limit`}`;
  });

  await step("checkpoint the finished world", async () => {
    const result = projectGit.checkpoint(PROJECT, "agentic e2e: world built and verified", false);
    return result.committed ? `committed ${result.fileCount} files at ${result.head}` : `nothing changed (${result.reason})`;
  });
} catch {
  // The first failure already printed; the summary below reports the outcome.
}

const total = timeline.reduce((sum, t) => sum + t.ms, 0);
console.log(`\n${passed}/${passed + failed} steps passed in ${(total / 1000).toFixed(0)}s`);
if (failed === 0) {
  const slowest = [...timeline].sort((a, b) => b.ms - a.ms).slice(0, 3);
  console.log(`slowest: ${slowest.map((s) => `${s.name} ${(s.ms / 1000).toFixed(1)}s`).join(" | ")}`);
  console.log("\nA small interactive VRChat world was scaffolded, scripted, verified and built with no human in the editor.");
}
process.exit(failed > 0 ? 1 : 0);
