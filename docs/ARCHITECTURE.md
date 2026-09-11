# Architecture

Why this toolchain is shaped the way it is. Every constraint below was verified against
Unity 2022.3.22f1 + VRChat Worlds SDK 3.10.5, not inferred.

## 1. Unity's own MCP is not an option here

Unity's first-party MCP ships in `com.unity.ai.assistant` and **requires Unity 6 (6000.0) or later**.
VRChat pins the Worlds SDK to Unity 2022.3 (`com.vrchat.worlds/package.json: "unity": "2022.3"`), so the
official server cannot be used at all. The generic Unity layer has to come from a community server
(AnkleBreaker `unity-mcp-server` and CoplayDev `unity-mcp` both support 2021.3+ including 2022.3), and
everything VRChat-specific is ours to build.

## 2. The build API cannot run in batch mode

The public world build surface is `VRC.SDK3.Editor.IVRCSdkWorldBuilderApi`
(`Build()`, `BuildAndTest()`, `BuildWithSignature()`, `BuildAndUpload(...)` — all `Task`-returning).
Reaching it goes through `VRCSdkControlPanel.TryGetBuilder<T>()`, which hard-fails unless the SDK control
panel `EditorWindow` exists:

```
VRCSdkControlPanelBuilder.cs:497  "Cannot get builder, SDK window is not open"
```

On top of that, `-batchmode -executeMethod` does not pump `EditorApplication.update`, which the SDK's
UniTask-based build needs in order to progress, and Unity 2022.3.x has known batch-mode startup hangs.
A grep for `isBatchMode|executeMethod|EditorApplication.Exit` across both SDK packages hits exactly one
line (`EnvConfig.cs:471`) — the SDK has no CI awareness whatsoever.

**Consequence:** a long-lived *GUI* editor with an in-process server, owned by a supervisor. Not batch mode.

## 3. Layers

```
MCP client (Claude Code / Codex)
   │  stdio
   ▼
packages/vrcw-mcp          Node/TS MCP server; discovers editors, retries through domain reloads
   │  HTTP 127.0.0.1 + token
   ▼
Packages/com.agentic.vrcw  Editor-side bridge: HttpListener + main-thread pump + jobs
   │
   ▼
Unity Editor (GUI)  ←owned by→  packages/supervisor
```

### Transport: HTTP, not a persistent socket

Every script change tears down the managed domain. A request/response protocol with no long-lived
connection survives that: the client simply retries and the reloaded domain serves the next call. Port
and token live in `SessionState`, so they are **stable across reloads** — otherwise every recompile would
move the endpoint and rotate the credential.

### Two liveness signals, deliberately

| Endpoint | Main thread? | Answers |
|---|---|---|
| `GET /ping` | no | Is the process alive, and is the pump ticking? |
| `POST /rpc` | most methods yes | Can the editor actually do work? |

This split is what lets the supervisor distinguish **"editor wedged"** (ping fine, rpc returns
`EDITOR_BUSY`) from **"editor gone"** (ping fails). They need opposite responses.

### Security

Bound to `127.0.0.1` only, and `/rpc` requires the `X-Agentic-Token` header whose value is written into
the registry file. The bridge compiles and runs arbitrary C# in the editor, so an unauthenticated local
port would be a plain code-execution hole for any process on the machine.

### Editor discovery

The bridge writes `%LOCALAPPDATA%\AgenticVrcw\instances\<project>-<hash>.json` with project path, port,
token and pid. Clients discover editors from that directory rather than a fixed port, because the port is
ephemeral and several projects may be open. A dead pid in a registry entry means the editor crashed
without withdrawing it; clients filter on process liveness.

## 4. Jobs, not blocking calls

UdonSharp compiles, world builds, lightmap bakes and multi-client tests run for minutes. Blocking would
exceed client timeouts *and* stall the very update loop the SDK's async build needs. So long work is
submitted as a job and polled.

- Job bodies are `IEnumerator`s stepped **once per editor update**, so `yield return null` means
  "resume next frame", exactly as in a Unity coroutine.
- `job.submit/status/list/cancel` are all answered **off** the main thread. If `job.status` needed the
  main thread it would queue behind the job it is reporting on and polling could never succeed.
- Every state change is journalled to `<project>/.agentic/jobs/<id>.json`. A job that was mid-flight when
  the domain reloaded is marked `interrupted` on the next load, rather than claiming to run forever.

## 5. eval_csharp: the reference set is the hard part

`unity.eval_csharp` compiles a snippet with the Mono CodeDom provider (VRChat forces
`ApiCompatibilityLevel.NET_4_6`, `EnvConfig.cs:241`). Two non-obvious things are required:

1. **`/noconfig /nostdlib+`.** Left alone, mcs adds its own default profile (mscorlib, System, System.Core,
   System.Xml from `MonoBleedingEdge/lib/mono/4.5`) on top of the editor's `unityjit-win32` ones. Every
   core type then exists twice: `error CS0433: The imported type 'System.AppDomain' is defined multiple times`.
2. **The netstandard *facade*, not the reference assembly.** VRChat and UdonSharp ship NuGet-built,
   netstandard-targeting DLLs (`System.Collections.Immutable`, `System.Reflection.Metadata`,
   `Newtonsoft.Json`). Without netstandard they fail with
   `error CS0012: The type 'System.Object' is defined in an assembly that is not referenced`.
   The fix is `<mono profile>/Facades/netstandard.dll` (~90KB of `TypeForwardedTo`, cannot collide), and
   explicitly **not** `Data/NetStandard/ref/2.1.0/netstandard.dll` (1.5MB of real definitions, which
   reintroduces CS0433).

A clean compile today emits **zero** warnings with 254 references. `includeReferences: true` dumps the
exact set when this needs re-diagnosing.

`unity.invoke_static` / `unity.get_static` exist alongside eval because they need no compiler and reach
`internal` members — which matters since the SDK grants `InternalsVisibleTo` only to its own assemblies,
with no third-party friend hook.

## 6. Unity install selection is a correctness issue, not a convenience

A machine can have several editors that all call themselves `2022.3.22f1` while only one is the build
VRChat supports. On this development machine:

| Path | Real `ProductVersion` | Usable |
|---|---|---|
| `E:\UnityEditor\Unity 2022.3.22f1` | `2022.3.22f1_887be4894c44` | yes (international) |
| `C:\Program Files\Unity\Hub\Editor\2022.3.22f1` | `2022.3.22f1c1_c3cbd310e76e` | no — **Unity China** |
| `E:\Unity\2022.3.22f1` | `2022.3.22f1c1_...` | no — China *and* missing licensing client |

The folder name and `metadata.hub.json` of the China install both just say `2022.3.22f1`; only the
executable's PE `ProductVersion` tells the truth. VRChat does not support Unity China editors, and opening
a VRChat project with one prompts a project migration that would rewrite `ProjectVersion.txt` — with
nobody present to decline it. An install missing
`Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client.exe` exits with code 199 before the project
even loads.

So `supervisor` reads the PE version, compares **version *and* revision** against
`ProjectSettings/ProjectVersion.txt`, flags the `cN` China suffix, and refuses to launch a mismatch unless
explicitly overridden. `vrcw-supervisor editors --project <path>` prints the verdict for every install.

## 7. Restart policy is asymmetric on purpose

- **Process died** → relaunch automatically. Nothing is lost that was not already lost.
- **Main thread not pumping** → report, do not kill, unless `--allow-wedge-restart true`. Lightmap bakes,
  large imports and synchronous UdonSharp compiles all legitimately freeze the pump for minutes; killing
  then would discard real work.

A freshly launched editor takes tens of seconds to rebuild its domain and register. Until it does, the
registry still holds the *previous* editor's dead pid, which reads as "dead". Without a startup grace
period the supervisor relaunches again and two editors fight over the same project lock — observed, then
fixed with `--startup-grace` (default 300s).

## 8. What ClientSim can and cannot do (for M3)

- The test framework was **removed upstream**: the ClientSim repo states "Removing the Tests folder from
  the source… they're not currently working", and `VRC.ClientSim.Tests` (with `ClientSimWorldTestBase`,
  `ClientSimTestHelpers`) is not shipped in the package — only samples that reference it.
- The **public test seam survives**, so a harness can be rebuilt on supported API:
  `ClientSimRuntimeLoader.BeginUnityTesting(settings, dispatcher)` / `EndUnityTesting()` /
  `IsInUnityTest()` are all `[PublicAPI] public static`, alongside `ClientSimMain.CreateInstance`,
  the public `IClientSimInput` interface, and `IClientSimEventDispatcher`.
- ClientSim is **single-player with no networking simulation**. Sync, ownership and late-joiner bugs can
  only be caught by `BuildAndTest()` with `VRCSettings.NumClients` (0–8) real clients, observed through
  `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt` with
  `--enable-udon-debug-logging --enable-sdk-log-levels --no-vr`.
- Udon cannot emit telemetry outbound (`VRCUrl` is not constructible at runtime), so `Debug.Log` is the
  only channel; the plan is tagged lines (`[AGENTTEL]{json}`) parsed from those logs.

## 9. UdonSharp ordering constraints (for M1)

Three traps that dictate the shape of the M1 tools:

1. **Two-stage compile.** `UdonSharpCompilerV1` checks `DoesUnityProjectHaveCompileErrors()` and then
   *silently returns doing nothing* (`UdonSharpCompilerV1.cs:272`). Unity's own C# compile must land
   first, so a domain reload sits between writing a `.cs` and compiling it.
2. **Version gates.** A freshly created `UdonSharpProgramAsset` has `ScriptVersion = Unknown`, and
   `CopyProxyToUdon` *throws* until `UdonSharpEditorManager.RunAllUpgrades()` plus a compile have run
   (`UdonSharpEditorUtility.cs:1172-1176`).
3. **Proxies are destroyed on build.** `[PostProcessScene]` destroys every `UdonSharpBehaviour` proxy when
   `isBuildingPlayer` (`UdonSharpEditorManager.cs:105-111`), so proxy-reading verification must not run
   inside a player build.

Also note `UdonSharpEditorUtility.ConvertToUdonBehaviours` is a dead stub that throws
`NotImplementedException`, and the `AddComponent` its obsolete message points at does not exist. The real
entry points are `UdonSharpUndo.AddComponent(go, type)` and `go.AddUdonSharpComponent(type)`.
