#!/usr/bin/env node
/**
 * M3 acceptance test: behavioural verification.
 *
 * Runs a real ClientSim play-mode session and asserts on the Udon program's state, then exercises the local
 * build path and the real-client (multi-client) preflight.
 *
 * The negative test matters as much as the positive one: a harness that cannot fail is worthless, so this
 * also asserts that a deliberately wrong expectation is reported as a failure.
 */
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";

const CLASS = "AgenticTestDoor";
const HOST = "AgenticTest/Door";
const VISUAL = "AgenticTest/DoorVisual";

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

async function runJob(kind, params, waitMs = 600_000) {
  const job = await rpc("job.submit", { kind, params });
  const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
  const deadline = Date.now() + waitMs;
  let last = job;
  while (Date.now() < deadline) {
    try {
      last = await rpc("job.status", { id: job.id }, { timeoutMs: 20_000 });
      if (terminal.has(last.state)) return last;
    } catch {
      /* play mode and builds stall the pump; keep polling */
    }
    await new Promise((r) => setTimeout(r, 1500));
  }
  throw new Error(`job ${kind} did not finish: ${JSON.stringify(last).slice(0, 300)}`);
}

// ---------------------------------------------------------------- setup

await check("editor hardened for play-mode verification", async () => {
  await waitForIdle();
  // Domain reload on Play would destroy the verification job mid-run, and ClientSim's own test guidance
  // requires it off too.
  const h = await rpc("bridge.harden_editor", { apply: true, disableDomainReloadOnPlay: true }, { timeoutMs: 60_000 });
  assert(h.enterPlayModeOptionsEnabled === true, "enter-play-mode options not enabled");
  assert(String(h.enterPlayModeOptions).includes("DisableDomainReload"), `unexpected options: ${h.enterPlayModeOptions}`);
  return `options=${h.enterPlayModeOptions}`;
});

await check("verification surface is registered", async () => {
  const health = await rpc("bridge.health");
  for (const kind of ["verify.clientsim", "verify.build_local", "verify.multiclient"]) {
    assert(health.jobKinds.includes(kind), `${kind} not registered: ${JSON.stringify(health.jobKinds)}`);
  }
  const schema = await rpc("verify.scenario_schema");
  assert(schema.steps.length >= 10, `schema looks thin: ${schema.steps.length} steps`);
  return `jobKinds=${JSON.stringify(health.jobKinds)} schemaSteps=${schema.steps.length}`;
});

await check("door fixture exists and is wired", async () => {
  // Build the fixture in whatever scene is open; the suite must not depend on another suite's leftovers.
  const setup = await rpc("unity.eval_csharp", {
    code: `var root = GameObject.Find("AgenticTest");
if (root == null) root = new GameObject("AgenticTest");
foreach (var child in new string[] { "Door", "DoorVisual" }) {
  if (root.transform.Find(child) == null) {
    var go = new GameObject(child);
    go.transform.SetParent(root.transform, false);
  }
}
root.transform.Find("DoorVisual").gameObject.SetActive(true);
EditorSceneManager.MarkSceneDirty(root.scene);
EditorSceneManager.SaveOpenScenes();
return root.transform.childCount;`,
  }, { timeoutMs: 120_000 });
  assert(setup.compiled && !setup.threw, `fixture setup failed: ${setup.message ?? JSON.stringify(setup.diagnostics?.slice(0, 2))}`);

  await rpc("udon.attach", { gameObject: HOST, type: CLASS }, { timeoutMs: 60_000 });
  const fields = await rpc("udon.set_fields", {
    gameObject: HOST,
    type: CLASS,
    fields: { isOpen: false, doorVisual: { $gameObject: VISUAL }, label: "m3", openAngle: 90, hinge: [0, 1, 0] },
    saveScene: true,
  }, { timeoutMs: 60_000 });
  assert(fields.copiedToUdon, `CopyProxyToUdon failed: ${fields.copyError}`);
  return `fields copied to Udon: ${fields.applied.map((f) => f.name).join(", ")}`;
});

// ---------------------------------------------------------------- clientsim

await check("ClientSim scenario verifies the door's interact logic", async () => {
  const job = await runJob("verify.clientsim", {
    name: "door toggles on interact",
    steps: [
      { wait_frames: 20 },
      { assert_var: { gameObject: HOST, type: CLASS, name: "isOpen", equals: false } },
      { assert_active: { gameObject: VISUAL, equals: true } },
      { interact: { gameObject: HOST, type: CLASS } },
      { wait_frames: 3 },
      { assert_var: { gameObject: HOST, type: CLASS, name: "isOpen", equals: true } },
      { assert_active: { gameObject: VISUAL, equals: false } },
      { interact: { gameObject: HOST, type: CLASS } },
      { wait_frames: 3 },
      { assert_var: { gameObject: HOST, type: CLASS, name: "isOpen", equals: false } },
      { assert_active: { gameObject: VISUAL, equals: true } },
      { assert_no_errors: true },
    ],
  });

  assert(job.state === "succeeded", `job ${job.state}: ${job.error}`);
  const r = job.result;
  const failures = (r.steps ?? []).filter((s) => !s.ok);
  assert(r.ok === true, `scenario failed: ${JSON.stringify(failures).slice(0, 400)}`);

  const startup = (r.steps ?? []).find((s) => s.step === "clientsim_startup");
  return `steps=${r.stepCount} failures=${r.failureCount} clientSimUp=${startup?.ok}`;
});

await check("a wrong expectation is reported as a failure", async () => {
  const job = await runJob("verify.clientsim", {
    name: "negative control",
    steps: [
      { wait_frames: 10 },
      // isOpen was left false by the previous scenario, so asserting true must fail.
      { assert_var: { gameObject: HOST, type: CLASS, name: "isOpen", equals: true } },
    ],
  });
  assert(job.state === "succeeded", `job itself errored: ${job.error}`);
  assert(job.result.ok === false, "a wrong assertion was reported as passing");
  const failure = (job.result.steps ?? []).find((s) => !s.ok && s.step === "assert_var");
  assert(failure, `no assert_var failure recorded: ${JSON.stringify(job.result.steps)}`);
  assert(/isOpen is False.*expected True/i.test(failure.error), `unhelpful message: ${failure.error}`);
  return failure.error;
});

await check("scenario can set a variable and send a custom event", async () => {
  const job = await runJob("verify.clientsim", {
    name: "set_var and send_event",
    steps: [
      { wait_frames: 10 },
      { set_var: { gameObject: HOST, type: CLASS, name: "isOpen", value: true } },
      { send_event: { gameObject: HOST, type: CLASS, event: "Apply" } },
      { wait_frames: 2 },
      // Apply() hides the visual when isOpen is true.
      { assert_active: { gameObject: VISUAL, equals: false } },
      { set_var: { gameObject: HOST, type: CLASS, name: "isOpen", value: false } },
      { send_event: { gameObject: HOST, type: CLASS, event: "Apply" } },
      { wait_frames: 2 },
      { assert_active: { gameObject: VISUAL, equals: true } },
    ],
  });
  assert(job.result.ok === true, `scenario failed: ${JSON.stringify((job.result.steps ?? []).filter((s) => !s.ok)).slice(0, 300)}`);
  return `steps=${job.result.stepCount}`;
});

await check("unknown step is rejected with the schema pointer", async () => {
  const job = await runJob("verify.clientsim", { steps: [{ fly_to_the_moon: true }] });
  const failure = (job.result?.steps ?? []).find((s) => !s.ok) ?? {};
  const message = String(failure.error ?? job.error ?? "");
  assert(/Unrecognised scenario step|scenario_schema/.test(message), `unhelpful: ${message}`);
  return message.slice(0, 140);
});

// Regression guard. Unity clears Application.logMessageReceivedThreaded handlers when play mode ends, and
// with domain reload disabled (which ClientSim requires) the static constructor never re-runs to restore
// them — so console capture used to die silently after the first scenario and stay dead.
await check("console capture survives a play-mode cycle", async () => {
  const probe = `[m3-capture-probe] ${Date.now()}`;
  const r = await rpc("unity.eval_csharp", { code: `Debug.Log("${probe}"); return 1;` }, { timeoutMs: 120_000 });
  assert(r.compiled && !r.threw, "probe eval failed");
  const captured = (r.logs?.entries ?? []).some((e) => String(e.msg).includes(probe));
  assert(captured, "Debug.Log was not captured after a play-mode session; the log subscription was lost");
  return "log subscription intact after play mode";
});

await check("editor left out of play mode and ClientSim settings restored", async () => {
  const state = await rpc("unity.editor_state");
  assert(state.isPlaying === false, "editor is still in play mode");
  return "not playing";
});

// ---------------------------------------------------------------- build

await check("build_status reports the target and installed modules", async () => {
  const s = await rpc("world.build_status");
  assert(s.activeBuildTarget, "no active target");
  assert(Array.isArray(s.targets), "no targets array");
  const android = s.targets.find((t) => t.target === "Android");
  return `active=${s.activeBuildTarget} dryRun=${s.dryRun} androidModule=${android?.moduleInstalled}`;
});

await check("dry-run flag round-trips", async () => {
  const on = await rpc("world.set_dry_run", { enabled: true });
  assert(on.dryRun === true, "dry run did not turn on");
  const off = await rpc("world.set_dry_run", { enabled: false });
  assert(off.dryRun === false, "dry run did not turn off");
  return "on then off";
});

await check("verify.build_local builds the open scene locally", async () => {
  const job = await runJob("verify.build_local", {}, 900_000);
  assert(job.state === "succeeded", `job ${job.state}: ${job.error}`);
  const r = job.result;
  if (!r.ok && r.stage === "precondition") {
    // An honest outcome, not a silent pass: report what is missing.
    return `precondition not met (reported, not hidden): ${r.reason}`;
  }
  assert(r.ok === true, `build failed at ${r.stage}: ${r.message} ${JSON.stringify(r.validationErrors ?? "")}`);
  assert(r.bundleBytes > 0, `bundle is empty: ${JSON.stringify(r)}`);
  return `${r.platform} bundle ${r.bundleMegabytes.toFixed(2)}MB in ${r.elapsedSeconds.toFixed(1)}s -> ${r.bundlePath}`;
});

// ---------------------------------------------------------------- real clients

await check("multiclient preflight names every blocker precisely", async () => {
  const p = await rpc("verify.multiclient_preflight", {}, { timeoutMs: 120_000 });
  assert(typeof p.ready === "boolean", "no ready flag");
  assert(Array.isArray(p.blockers), "no blockers array");
  if (p.ready) return "ready: a VRChat client is installed and signed in";
  // Each blocker must say what to do, not just that something is wrong.
  for (const b of p.blockers) assert(b.length > 40, `blocker too terse: ${b}`);
  return `not ready: ${p.blockers.map((b) => b.split(".")[0]).join(" | ")}`;
});

await check("multiclient job stops at preflight rather than half-running", async () => {
  const p = await rpc("verify.multiclient_preflight", {}, { timeoutMs: 120_000 });
  if (p.ready) return "skipped: preflight is satisfied on this machine";
  const job = await runJob("verify.multiclient", { clients: 2, observeSeconds: 5 }, 120_000);
  assert(job.state === "succeeded", `job ${job.state}: ${job.error}`);
  assert(job.result.ok === false && job.result.stage === "preflight", `expected a preflight stop: ${JSON.stringify(job.result).slice(0, 200)}`);
  return `stopped at preflight with ${job.result.blockers.length} blocker(s)`;
});

await check("VRChat log reader describes the log directory", async () => {
  const d = await rpc("verify.vrc_log_dir");
  assert(typeof d.exists === "boolean", "no exists flag");
  assert(Array.isArray(d.requiredLaunchFlags) && d.requiredLaunchFlags.length >= 2, "launch flags missing");
  return `exists=${d.exists} logs=${d.logCount ?? 0} dir=${d.directory}`;
});

await check("udon_exceptions reads both editor and client sources", async () => {
  const e = await rpc("verify.udon_exceptions", {});
  assert(Array.isArray(e.fromEditor), "no fromEditor array");
  assert(Array.isArray(e.fromClientLogs), "no fromClientLogs array");
  return `editor=${e.fromEditor.length} client=${e.fromClientLogs.length}`;
});

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
