#!/usr/bin/env node
/**
 * M0 acceptance test: drives a live Unity Editor through the same client code the MCP server uses.
 *
 * Run with the editor already open on a project that has com.agentic.vrcw installed:
 *   node tests/m0-smoke.mjs
 */
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { ping, rpc } from "../packages/vrcw-mcp/dist/bridge.js";

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
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 4).join("\n      ")}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

/** Runs a snippet and fails unless it both compiled and ran without throwing. */
async function evalOk(code, usings = []) {
  const r = await rpc("unity.eval_csharp", { code, usings }, { timeoutMs: 180_000 });
  const errors = (r.diagnostics ?? []).filter((d) => d.severity === "error");
  assert(r.compiled, `did not compile: ${JSON.stringify(errors.slice(0, 3))}`);
  assert(!r.threw, `threw ${r.exception}: ${r.message}`);
  return r;
}

/**
 * Waits until the editor is serving and not mid-compile.
 *
 * Any agent workflow needs this gate, not just the test: writing a script triggers a recompile and then
 * a domain reload, during which the listener is closed and calls fail at the transport level. rpc()
 * retries through that window, but a point-in-time probe like ping() does not.
 */
async function waitForIdleEditor(budgetMs = 300_000) {
  const deadline = Date.now() + budgetMs;
  while (Date.now() < deadline) {
    try {
      const c = await rpc("unity.compile_status", {}, { timeoutMs: 20_000, reconnectWindowMs: budgetMs });
      if (!c.isCompiling && !c.isUpdatingAssets) return c;
    } catch {
      // EDITOR_BUSY / transport failure while the domain reloads.
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error(`editor did not become idle within ${budgetMs}ms`);
}

await check("editor becomes idle and serving", async () => {
  const c = await waitForIdleEditor();
  return `isCompiling=${c.isCompiling} errors=${c.errorCount}`;
});

// ---------------------------------------------------------------- transport

await check("ping answers without the main thread", async () => {
  const p = await ping();
  assert(p.alive === true, "not alive");
  assert(Number(p.pumpTicks) > 0, "pump has never ticked");
  return `pid=${p.pid} port=${p.port} pumpTicks=${p.pumpTicks}`;
});

await check("rejects a wrong token", async () => {
  const dir = join(process.env.LOCALAPPDATA, "AgenticVrcw", "instances");
  const file = join(dir, readdirSync(dir).find((f) => f.endsWith(".json")));
  const info = JSON.parse(readFileSync(file, "utf8"));
  const res = await fetch(`http://127.0.0.1:${info.port}/rpc`, {
    method: "POST",
    headers: { "content-type": "application/json", "x-agentic-token": "wrong" },
    body: JSON.stringify({ method: "bridge.health", params: {} }),
  });
  assert(res.status === 401, `expected 401, got ${res.status}`);
  return "401 as expected";
});

await check("bridge.health reports a live pump", async () => {
  const h = await rpc("bridge.health");
  assert(h.methodCount >= 14, `only ${h.methodCount} methods registered`);
  assert(Number(h.secondsSincePump) < 10, `pump stale by ${h.secondsSincePump}s`);
  return `methods=${h.methodCount} jobKinds=${JSON.stringify(h.jobKinds)}`;
});

// ---------------------------------------------------------------- observation

await check("editor_state sees the scene and VRChat preconditions", async () => {
  const s = await rpc("unity.editor_state");
  assert(s.unityVersion, "no unity version");
  assert(Array.isArray(s.scenes), "no scenes array");
  assert(s.vrchat && "sdkPanelOpen" in s.vrchat, "no vrchat probe");
  return `unity=${s.unityVersion} target=${s.activeBuildTarget} vrchat=${JSON.stringify(s.vrchat)}`;
});

await check("compile_status returns structured diagnostics", async () => {
  const c = await rpc("unity.compile_status");
  assert(typeof c.errorCount === "number", "no errorCount");
  assert(Array.isArray(c.diagnostics), "no diagnostics array");
  assert(c.errorCount === 0, `project has ${c.errorCount} C# errors: ${JSON.stringify(c.diagnostics.slice(0, 3))}`);
  return `errors=${c.errorCount} warnings=${c.warningCount} source=${c.source}`;
});

await check("console is readable and sequence-numbered", async () => {
  const first = await rpc("unity.console", { limit: 5 });
  assert(typeof first.latestSeq === "number", "no latestSeq");
  const second = await rpc("unity.console", { sinceSeq: first.latestSeq, limit: 5 });
  assert(second.returned === 0 || second.entries.every((e) => e.seq > first.latestSeq), "sinceSeq not honoured");
  return `latestSeq=${first.latestSeq}`;
});

// ---------------------------------------------------------------- execution

await check("eval: bare expression", async () => {
  const r = await evalOk("Application.unityVersion");
  assert(typeof r.value === "string" && r.value.length > 0, "no version returned");
  return `value=${r.value} refs=${r.referenceCount}`;
});

// Regression guard. Referencing the whole domain naively made every core type ambiguous
// (CS0433 on System.Object / AppDomain / Dictionary), so these specific types are the canary.
await check("eval: AppDomain + Dictionary + Linq compile cleanly (CS0433 regression)", async () => {
  const r = await evalOk(
    `Debug.Log("[m0-smoke] eval reached Unity");
var vrc = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name.StartsWith("VRC"));
return new Dictionary<string, object> { { "vrcAssemblies", vrc }, { "scenes", EditorSceneManager.sceneCount } };`
  );
  const warnings = (r.diagnostics ?? []).filter((d) => d.severity === "warning");
  assert(warnings.length === 0, `expected a clean compile, got ${warnings.length} warnings`);
  assert(Number(r.value.vrcAssemblies) > 0, "no VRChat assemblies visible");
  const logged = (r.logs?.entries ?? []).some((e) => String(e.msg).includes("[m0-smoke]"));
  assert(logged, "Debug.Log output was not captured back");
  return `value=${JSON.stringify(r.value)} warnings=0`;
});

await check("eval: reaches VRChat SDK scene types", async () => {
  const r = await evalOk(
    `var found = Resources.FindObjectsOfTypeAll<VRC.SDK3.Components.VRCSceneDescriptor>();
var names = new List<string>();
foreach (var d in found) names.Add(d.gameObject.name + " spawns=" + (d.spawns == null ? 0 : d.spawns.Length));
return names;`,
    ["VRC.SDKBase", "VRC.SDK3.Components"]
  );
  assert(Array.isArray(r.value), "expected a list");
  return `descriptors=${JSON.stringify(r.value)}`;
});

await check("eval: reaches UdonSharp editor API", async () => {
  const r = await evalOk(
    `return new Dictionary<string, object> {
  { "programAssets", UdonSharp.UdonSharpProgramAsset.GetAllUdonSharpPrograms().Length },
  { "anyScriptHasError", UdonSharp.UdonSharpProgramAsset.AnyUdonSharpScriptHasError() }
};`,
    ["UdonSharp", "UdonSharpEditor"]
  );
  assert(r.value.anyScriptHasError === false, "UdonSharp reports script errors");
  return `programAssets=${r.value.programAssets}`;
});

await check("eval: a compile error comes back structured, not as a crash", async () => {
  const r = await rpc("unity.eval_csharp", { code: "return thisDoesNotExist;" }, { timeoutMs: 120_000 });
  assert(r.compiled === false, "expected compilation to fail");
  const errors = (r.diagnostics ?? []).filter((d) => d.severity === "error");
  assert(errors.length > 0, "no error diagnostics");
  assert(errors[0].code === "CS0103", `expected CS0103, got ${errors[0].code}`);
  assert(errors[0].line === 1, `line should map to the snippet, got ${errors[0].line}`);
  return `${errors[0].code} at line ${errors[0].line}: ${errors[0].message}`;
});

await check("eval: a runtime throw is reported, not swallowed", async () => {
  const r = await rpc("unity.eval_csharp", { code: "throw new InvalidOperationException(\"boom\");" }, { timeoutMs: 120_000 });
  assert(r.compiled === true, "should have compiled");
  assert(r.threw === true, "should have reported a throw");
  assert(String(r.message).includes("boom"), `unexpected message: ${r.message}`);
  return `${r.exception}: ${r.message}`;
});

// ---------------------------------------------------------------- reflection

await check("invoke_static reaches a VRChat SDK static (no compiler involved)", async () => {
  const syncable = await rpc("unity.invoke_static", {
    type: "VRC.Udon.UdonNetworkTypes",
    method: "CanSync",
    args: ["System.String"],
  });
  const notSyncable = await rpc("unity.invoke_static", {
    type: "VRC.Udon.UdonNetworkTypes",
    method: "CanSync",
    args: ["UnityEngine.Vector3Int"],
  });
  assert(syncable.returned === true, "string should be syncable");
  assert(notSyncable.returned === false, "Vector3Int should not be syncable");
  return `${syncable.signature}: string=true Vector3Int=false`;
});

await check("invoke_static explains a bad method instead of failing opaquely", async () => {
  try {
    await rpc("unity.invoke_static", { type: "VRC.Udon.UdonNetworkTypes", method: "NoSuchMethod", args: [] });
    throw new Error("expected an error");
  } catch (err) {
    assert(/No static method|Available/.test(err.message), `unhelpful message: ${err.message}`);
    return "reported available overloads";
  }
});

await check("get_static reads a static property", async () => {
  const r = await rpc("unity.get_static", { type: "UnityEditor.EditorApplication", member: "applicationContentsPath" });
  assert(typeof r.value === "string" && r.value.length > 0, "no value");
  return `${r.kind} ${r.member} = ${r.value}`;
});

// ---------------------------------------------------------------- jobs

await check("job runs on the update pump, is pollable while running, and completes", async () => {
  const job = await rpc("job.submit", { kind: "debug.tick", params: { ticks: 40 } });
  assert(job.id, "no job id");

  const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
  let sawRunning = false;
  let last = null;
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    last = await rpc("job.status", { id: job.id });
    if (last.state === "running") sawRunning = true;
    if (terminal.has(last.state)) break;
    await new Promise((r) => setTimeout(r, 300));
  }

  assert(last && terminal.has(last.state), `job never finished (state=${last?.state})`);
  assert(last.state === "succeeded", `job ended ${last.state}: ${last.error}`);
  assert(last.result && last.result.ticks === 40, `unexpected result ${JSON.stringify(last.result)}`);
  return `state=${last.state} progress=${last.progress} observedRunning=${sawRunning}`;
});

await check("job.cancel stops a running job", async () => {
  const job = await rpc("job.submit", { kind: "debug.tick", params: { ticks: 100000 } });
  await new Promise((r) => setTimeout(r, 400));
  const cancel = await rpc("job.cancel", { id: job.id });
  assert(cancel.cancelRequested === true, "cancel was not accepted");

  const deadline = Date.now() + 20_000;
  let last = null;
  while (Date.now() < deadline) {
    last = await rpc("job.status", { id: job.id });
    if (last.state === "cancelled") break;
    await new Promise((r) => setTimeout(r, 300));
  }
  assert(last?.state === "cancelled", `expected cancelled, got ${last?.state}`);
  return `state=${last.state}`;
});

await check("unknown job kind names the known kinds", async () => {
  try {
    await rpc("job.submit", { kind: "no.such.kind", params: {} });
    throw new Error("expected an error");
  } catch (err) {
    assert(/Unknown job kind/.test(err.message), `unhelpful message: ${err.message}`);
    return "reported known kinds";
  }
});

await check("unknown method is reported as UNKNOWN_METHOD", async () => {
  try {
    await rpc("no.such.method");
    throw new Error("expected an error");
  } catch (err) {
    assert(/UNKNOWN_METHOD|No such bridge method/.test(err.message), `unexpected: ${err.message}`);
    return "UNKNOWN_METHOD";
  }
});

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
