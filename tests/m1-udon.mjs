#!/usr/bin/env node
/**
 * M1 acceptance test: the full UdonSharp authoring loop with no human touching Unity.
 *
 *   write .cs -> Unity compiles -> program asset -> UdonSharp compile -> attach -> set fields -> read back
 *
 * The read-back is the real assertion. Values set on the proxy MonoBehaviour do not ship unless
 * CopyProxyToUdon moves them onto the hidden backing UdonBehaviour, so the test checks both stores
 * agree; a mismatch is exactly the silent data loss this layer exists to prevent.
 */
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";

const CLASS = "AgenticTestDoor";
const FOLDER = "Assets/Udon";
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

async function runJob(kind, params, waitMs = 240_000) {
  const job = await rpc("job.submit", { kind, params });
  const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
  const deadline = Date.now() + waitMs;
  let last = null;
  while (Date.now() < deadline) {
    try {
      last = await rpc("job.status", { id: job.id }, { timeoutMs: 20_000 });
      if (terminal.has(last.state)) return last;
    } catch {
      /* reload during the job */
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`job ${kind} did not finish: ${JSON.stringify(last)}`);
}

const SOURCE = `using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ${CLASS} : UdonSharpBehaviour
{
    public GameObject doorVisual;
    public float openAngle;
    public string label;
    public Vector3 hinge;

    [UdonSynced] public bool isOpen;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        isOpen = !isOpen;
        RequestSerialization();
        Apply();
    }

    public override void OnDeserialization()
    {
        Apply();
    }

    public void Apply()
    {
        if (doorVisual != null) doorVisual.SetActive(!isOpen);
    }
}
`;

// Remove the editor's interactive prompts before touching assets. A modal dialog halts Unity's update
// loop and therefore the bridge's pump, which would stall the whole suite with nobody to click the button.
await check("editor prompts are disabled", async () => {
  const h = await rpc("bridge.harden_editor", { apply: true }, { timeoutMs: 60_000 });
  const bad = (h.settings ?? []).filter((s) => !s.compliant).map((s) => s.key);
  assert(bad.length === 0, `still interactive: ${bad.join(", ")}`);
  return `changed=${JSON.stringify(h.changed)}`;
});


// ---------------------------------------------------------------- setup

await check("editor is idle and the Udon surface is registered", async () => {
  await waitForIdle();
  const health = await rpc("bridge.health");
  assert(health.jobKinds.includes("udon.compile"), `udon.compile not registered: ${JSON.stringify(health.jobKinds)}`);
  const status = await rpc("udon.status");
  return `methods=${health.methodCount} programAssets=${status.programAssetCount} anyScriptHasError=${status.anyScriptHasError}`;
});

await check("test GameObjects exist in the scene", async () => {
  const r = await rpc("unity.eval_csharp", {
    code: `var root = GameObject.Find("AgenticTest");
if (root == null) root = new GameObject("AgenticTest");
foreach (var child in new string[] { "Door", "DoorVisual" }) {
  if (root.transform.Find(child) == null) {
    var go = new GameObject(child);
    go.transform.SetParent(root.transform, false);
  }
}
EditorSceneManager.MarkSceneDirty(root.scene);
return root.transform.childCount;`,
  }, { timeoutMs: 120_000 });
  assert(r.compiled && !r.threw, `setup eval failed: ${JSON.stringify(r.diagnostics?.slice(0, 2))} ${r.message ?? ""}`);
  return `children=${r.value}`;
});

// ---------------------------------------------------------------- authoring loop

await check("create_script writes the .cs", async () => {
  const r = await rpc("udon.create_script", { className: CLASS, folder: FOLDER, code: SOURCE }, { timeoutMs: 120_000 });
  assert(r.scriptPath === `${FOLDER}/${CLASS}.cs`, `unexpected path ${r.scriptPath}`);
  return `typeAvailable=${r.typeAvailable} programAssetCreated=${r.programAssetCreated}`;
});

await check("create_script rejects a class name that does not match the file", async () => {
  try {
    await rpc("udon.create_script", { className: "AgenticMismatch", folder: FOLDER, code: "public class Other {}" });
    throw new Error("expected a rejection");
  } catch (err) {
    assert(/class name to match the file name/.test(err.message), `unhelpful: ${err.message}`);
    return "rejected, as UdonSharp requires";
  }
});

await check("Unity compiles the new script cleanly", async () => {
  await rpc("unity.refresh_assets", { saveAssets: true, forceSynchronous: true }, { timeoutMs: 180_000 }).catch(() => {});
  const c = await waitForIdle();
  assert(c.errorCount === 0, `C# errors: ${JSON.stringify(c.diagnostics?.slice(0, 3))}`);
  return `errors=${c.errorCount} warnings=${c.warningCount}`;
});

await check("create_script is idempotent and now creates the program asset", async () => {
  const r = await rpc("udon.create_script", { className: CLASS, folder: FOLDER, code: SOURCE }, { timeoutMs: 120_000 });
  assert(r.typeAvailable, "Unity still cannot see the type");
  assert(r.programAssetExists, "program asset was not created");
  return `programAssetExists=${r.programAssetExists} created=${r.programAssetCreated}`;
});

await check("udon.compile job succeeds", async () => {
  // The upgrade pass can rewrite assets and trigger a reload, which interrupts the job by design.
  // Resubmitting is the documented response, so the test does what an agent should do.
  let job = await runJob("udon.compile", {});
  if (job.state === "interrupted" || (job.result && job.result.stage === "upgrade")) {
    await waitForIdle();
    job = await runJob("udon.compile", {});
  }
  assert(job.state === "succeeded", `job ${job.state}: ${job.error}`);
  assert(job.result.ok === true, `compile not ok: ${JSON.stringify(job.result).slice(0, 400)}`);
  return `stage=${job.result.stage} errors=${job.result.errorCount} warnings=${job.result.warningCount}`;
});

await check("attach wires a backing UdonBehaviour and links the program asset", async () => {
  const r = await rpc("udon.attach", { gameObject: HOST, type: CLASS }, { timeoutMs: 60_000 });
  assert(r.hasBackingUdonBehaviour, "no backing UdonBehaviour was created");
  assert(r.programAssetLinked, "program asset not linked to the backing behaviour");
  const synced = r.publicFields.filter((f) => f.synced).map((f) => f.name);
  assert(synced.includes("isOpen"), `[UdonSynced] field not detected: ${JSON.stringify(r.publicFields)}`);
  return `fields=${r.publicFields.length} synced=${JSON.stringify(synced)} alreadyPresent=${r.alreadyPresent}`;
});

await check("set_fields applies every value type and copies them to Udon", async () => {
  const r = await rpc("udon.set_fields", {
    gameObject: HOST,
    type: CLASS,
    fields: {
      openAngle: 82.5,
      label: "west gate",
      hinge: { x: 0, y: 1, z: 0 },
      isOpen: true,
      doorVisual: { $gameObject: VISUAL },
    },
    saveScene: true,
  }, { timeoutMs: 60_000 });
  assert(r.copiedToUdon, `CopyProxyToUdon failed: ${r.copyError} ${r.hint ?? ""}`);
  assert(r.applied.length === 5, `expected 5 fields, got ${r.applied.length}`);
  return `applied=${r.applied.map((f) => f.name).join(", ")}`;
});

await check("read_vars shows proxy and UdonBehaviour in agreement", async () => {
  const r = await rpc("udon.read_vars", { gameObject: HOST, type: CLASS });
  assert(r.hasBackingUdonBehaviour, "no backing behaviour");
  assert(r.mismatchCount === 0, `${r.mismatchCount} mismatch(es): ${JSON.stringify(r.variables)}`);

  const byName = Object.fromEntries(r.variables.map((v) => [v.name, v]));
  assert(Math.abs(byName.openAngle.proxyValue - 82.5) < 0.001, `openAngle=${byName.openAngle.proxyValue}`);
  assert(byName.label.proxyValue === "west gate", `label=${byName.label.proxyValue}`);
  assert(byName.isOpen.proxyValue === true, `isOpen=${byName.isOpen.proxyValue}`);
  assert(byName.hinge.proxyValue.y === 1, `hinge=${JSON.stringify(byName.hinge.proxyValue)}`);
  assert(byName.doorVisual.proxyValue && byName.doorVisual.proxyValue.name === "DoorVisual",
    `doorVisual=${JSON.stringify(byName.doorVisual.proxyValue)}`);

  const onUdon = r.variables.filter((v) => v.presentOnUdonBehaviour).map((v) => v.name);
  assert(onUdon.length >= 5, `only ${onUdon.length} value(s) reached the UdonBehaviour: ${JSON.stringify(onUdon)}`);
  return `presentOnUdon=${JSON.stringify(onUdon)} mismatches=0`;
});

await check("set_fields names the available fields when given a bad one", async () => {
  try {
    await rpc("udon.set_fields", { gameObject: HOST, type: CLASS, fields: { nope: 1 } });
    throw new Error("expected a rejection");
  } catch (err) {
    assert(/Public fields:/.test(err.message), `unhelpful: ${err.message}`);
    return "listed the real fields";
  }
});

await check("list_behaviours finds the attached behaviour", async () => {
  const r = await rpc("udon.list_behaviours");
  const mine = r.behaviours.find((b) => b.gameObject.endsWith(HOST) && b.type === CLASS);
  assert(mine, `not listed: ${JSON.stringify(r.behaviours).slice(0, 300)}`);
  assert(mine.programAssetLinked, "listed but program asset not linked");
  return `count=${r.count} mine=${mine.gameObject}`;
});

await check("attach is idempotent", async () => {
  const r = await rpc("udon.attach", { gameObject: HOST, type: CLASS });
  assert(r.alreadyPresent, "second attach created a duplicate instead of reporting the existing one");
  return "reported the existing component";
});

await check("helpful error for an unknown behaviour type", async () => {
  try {
    await rpc("udon.attach", { gameObject: HOST, type: "NoSuchBehaviour" });
    throw new Error("expected a rejection");
  } catch (err) {
    assert(/No non-abstract UdonSharpBehaviour named/.test(err.message), `unhelpful: ${err.message}`);
    return "explained, including the compile-first hint";
  }
});

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
