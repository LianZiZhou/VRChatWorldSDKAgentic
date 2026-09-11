#!/usr/bin/env node
/**
 * Fast-feedback layer: the out-of-Unity Roslyn linter and the Udon exposure whitelist.
 *
 * Both exist to avoid paying a write/compile/domain-reload cycle to learn something knowable cheaply. The
 * tests assert both directions: that the checks fire on bad code, and that they stay quiet on good code —
 * a linter that flags a correct script is worse than no linter, because it teaches people to ignore it.
 */
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";

const here = dirname(fileURLToPath(import.meta.url));
const analyzer = join(here, "..", "packages", "udon-analyzer", "bin", "Release", "net8.0",
  process.platform === "win32" ? "udon-analyzer.exe" : "udon-analyzer");
const fixtures = join(here, "fixtures");

let passed = 0;
let failed = 0;

function check(name, fn) {
  try {
    const detail = fn();
    passed++;
    console.log(`PASS  ${name}`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 5).join("\n      ")}`);
  }
}

async function checkAsync(name, fn) {
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

function lint(...paths) {
  const result = spawnSync(analyzer, ["--json", ...paths], { encoding: "utf8", timeout: 120_000 });
  if (!result.stdout) throw new Error(`analyzer produced no output (code ${result.status}): ${result.stderr}`);
  return JSON.parse(result.stdout);
}

// ---------------------------------------------------------------- fixtures

mkdirSync(fixtures, { recursive: true });

writeFileSync(join(fixtures, "BadUdon.cs"), `using System.Collections.Generic;
using System.Linq;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class BadUdon : UdonSharpBehaviour
{
    public static int sharedCounter;
    [UdonSynced] public int[] scores;
    [UdonSynced] public Vector3Int tile;
    private List<int> items;
    private int[,] grid;

    public BadUdon() { }

    void Start()
    {
        try { Debug.Log("x"); } catch { }
        var filtered = from i in new int[] { 1, 2 } select i;
        System.Func<int, int> add = x => x + 1;
        StartCoroutine("Nope");
        throw new System.Exception("no");
    }

    public T Echo<T>(T value) { return value; }
    public void NetworkReachable() { }
}
`);

writeFileSync(join(fixtures, "GoodUdon.cs"), `using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GoodUdon : UdonSharpBehaviour
{
    public GameObject target;
    public float speed;
    public const int MaxTries = 3;

    [UdonSynced] public bool isOn;
    [UdonSynced] public Vector3 offset;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        isOn = !isOn;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        if (target != null) target.SetActive(isOn);
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (player != null) _Apply();
    }
}
`);

// ---------------------------------------------------------------- linter

check("analyzer is built", () => {
  assert(existsSync(analyzer), `not built at ${analyzer}. Run: dotnet build -c Release packages/udon-analyzer/UdonAnalyzer.csproj`);
  return analyzer;
});

check("linter catches every constraint UdonSharp rejects", () => {
  const report = lint(join(fixtures, "BadUdon.cs"));
  const rules = new Set(report.diagnostics.filter((d) => d.Severity === "error").map((d) => d.Rule));

  const expected = [
    "no-static-fields",
    "synced-array-needs-manual",
    "type-not-syncable",
    "generic-not-in-udon",
    "no-multidim-array",
    "no-constructors",
    "no-try-catch",
    "no-linq",
    "no-lambda",
    "no-coroutines",
    "no-throw",
    "no-generic-methods",
  ];
  const missing = expected.filter((r) => !rules.has(r));
  assert(missing.length === 0, `rules did not fire: ${missing.join(", ")} (fired: ${[...rules].join(", ")})`);
  assert(report.errorCount >= expected.length, `only ${report.errorCount} errors`);
  return `${report.errorCount} errors, ${report.warningCount} warnings, ${rules.size} distinct rules`;
});

check("linter stays silent on a correct script", () => {
  const report = lint(join(fixtures, "GoodUdon.cs"));
  assert(report.errorCount === 0, `false positives: ${JSON.stringify(report.diagnostics.filter((d) => d.Severity === "error"))}`);
  // Its public methods are all real VRChat events or underscore-prefixed, so there should be no
  // implicit-network-entry noise either.
  const entries = report.diagnostics.filter((d) => d.Rule === "implicit-network-entry");
  assert(entries.length === 0, `flagged real VRChat events as network entries: ${JSON.stringify(entries)}`);
  return `clean: ${report.errorCount} errors, ${report.warningCount} warnings`;
});

check("linter flags a genuinely network-reachable method", () => {
  const report = lint(join(fixtures, "BadUdon.cs"));
  const entry = report.diagnostics.find((d) => d.Rule === "implicit-network-entry");
  assert(entry, "a public parameterless non-event method was not flagged");
  assert(entry.Message.includes("NetworkReachable"), `flagged the wrong method: ${entry.Message}`);
  return entry.Message.slice(0, 130);
});

check("linter reports sync budget and exits non-zero on findings", () => {
  const report = lint(fixtures);
  assert(report.filesAnalyzed >= 2, `expected both fixtures, analysed ${report.filesAnalyzed}`);
  const result = spawnSync(analyzer, ["--json", fixtures], { encoding: "utf8" });
  assert(result.status === 1, `expected exit 1 with findings, got ${result.status}`);
  return `analysed ${report.filesAnalyzed} files, exit code 1`;
});

check("linter is fast enough to run on every edit", () => {
  const started = Date.now();
  lint(fixtures);
  const elapsed = Date.now() - started;
  // The whole point is beating a Unity round trip, which is tens of seconds.
  assert(elapsed < 5000, `took ${elapsed}ms, which is too slow to run per edit`);
  return `${elapsed}ms for ${2} files`;
});

// ---------------------------------------------------------------- whitelist

await checkAsync("whitelist finds a real Udon API with its mangled name", async () => {
  const r = await rpc("udon.whitelist_query", { query: "Networking.__SetOwner", limit: 5 }, { timeoutMs: 120_000 });
  assert(r.matchCount >= 1, `SetOwner not found: ${JSON.stringify(r)}`);
  return r.matches[0];
});

await checkAsync("whitelist reports an API that genuinely is not in Udon", async () => {
  const r = await rpc("udon.whitelist_query", { query: "System.Threading.Thread", limit: 3 }, { timeoutMs: 120_000 });
  assert(r.matchCount === 0, `Thread should not be exposed: ${JSON.stringify(r.matches)}`);
  assert(r.hint, "no hint explaining that absence is the answer");
  return r.hint.slice(0, 130);
});

await checkAsync("event lookup works", async () => {
  const r = await rpc("udon.whitelist_query", { query: "Interact", kind: "event", limit: 5 }, { timeoutMs: 120_000 });
  assert(r.matches.includes("Event_Interact"), `Event_Interact missing: ${JSON.stringify(r.matches)}`);
  return r.matches.join(", ");
});

await checkAsync("syncability comes from the SDK and matches its rules", async () => {
  const cases = [
    ["System.String", true, false, false],
    ["UnityEngine.Vector3", true, true, true],
    ["UnityEngine.Color", true, true, false],
    ["UnityEngine.Vector3Int", false, false, false],
  ];
  const lines = [];
  for (const [type, canSync, linear, smooth] of cases) {
    const r = await rpc("udon.syncable", { type });
    assert(r.canSync === canSync, `${type}: canSync=${r.canSync}, expected ${canSync}`);
    assert(r.canSyncLinear === linear, `${type}: linear=${r.canSyncLinear}, expected ${linear}`);
    assert(r.canSyncSmooth === smooth, `${type}: smooth=${r.canSyncSmooth}, expected ${smooth}`);
    lines.push(`${type.split(".").pop()}=${r.canSync}/${r.canSyncLinear}/${r.canSyncSmooth}`);
  }
  return lines.join(" ");
});

await checkAsync("the linter and the SDK agree about what is syncable", async () => {
  // The linter has its own hardcoded table, so it can drift from the SDK. This is the check that catches
  // that drift rather than letting the two quietly disagree.
  const report = lint(join(fixtures, "BadUdon.cs"));
  const notSyncable = report.diagnostics.find((d) => d.Rule === "type-not-syncable");
  assert(notSyncable, "linter did not flag Vector3Int");

  const sdk = await rpc("udon.syncable", { type: "UnityEngine.Vector3Int" });
  assert(sdk.canSync === false, "SDK says Vector3Int IS syncable, so the linter's table is wrong");
  return "both agree Vector3Int is not syncable";
});

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
