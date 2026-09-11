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

## 10. Modal dialogs: the real blocker for unattended runs

This is the failure that actually stopped runs twice during development, and it is worth its own section.

A modal dialog halts Unity's update loop. That halts the bridge's main-thread pump, so every `/rpc` returns
`EDITOR_BUSY` and the run stops with nobody to click the button. Observed verbatim:

- **"The open scene(s) have been modified externally"** — raised by re-importing a scene that Unity itself just
  saved, and by rewriting scene files under a running editor (which is what a rollback does).
- **"Detected external changes"** — exists only when Asset Pipeline *Auto Refresh* is off.

Defended at four levels, because prevention alone cannot cover every dialog Unity might raise:

1. **Prevented at the cause.** `world.scaffold` no longer calls `AssetDatabase.ImportAsset` on the scene it
   just saved — `SaveScene` already registers it, and re-importing is what convinced Unity the open scene had
   changed behind its back. `bridge.harden_editor` sets `kAutoRefresh=1`, `DirectoryMonitoring=1` and
   `VerifySavingAssets=false`.
2. **Survivable.** Requests are handled on the thread pool. Handling them inline on the accept thread meant one
   `/rpc` waiting on a blocked main thread occupied that thread for its whole timeout, so `/ping` could not be
   accepted either — and a *blocked* editor looked *dead*, the exact opposite of what `/ping` exists for.
3. **Diagnosable.** The supervisor enumerates the editor's owned top-level windows (`list-windows.ps1`) and
   names the dialog. `Get-Process` only exposes `MainWindowTitle`, which is precisely the window that is not
   the dialog.
4. **Recoverable.** `--auto-dismiss-dialogs true` answers allowlisted dialogs by pressing their default button.
   The allowlist covers the benign external-change and save prompts and deliberately **holds** destructive ones
   such as "Delete selected asset?" — pressing the default button of an unrecognised dialog is a way to agree
   to something nobody asked for.

`tests/dialog-recovery.mjs` raises a real modal on purpose and asserts the whole chain.

## 11. Play mode, domain reload, and what it costs

`verify.clientsim` runs as a job stepped by `EditorApplication.update`, which only survives entering play mode
because `EnterPlayModeOptions.DisableDomainReload` is set. ClientSim's own test guidance requires the same
option. Two consequences follow, both of which had to be handled rather than wished away:

**SDK statics keep objects from the previous session.** Every repeated play-mode session logs a
`MissingReferenceException` from `Texture2DDefaultTextureHolder.ResetTextures`, and another from UdonSharp's
deferred `RunBehaviourSetup` `delayCall`. Neither is the world's bug. `assert_no_errors` reports them under
`ignored` with an explanation instead of failing — an assertion that fails on every run teaches a reader to
ignore it, which is worse than not having it.

**Log subscriptions are cleared on play-mode exit.** Unity clears
`Application.logMessageReceivedThreaded` handlers when play mode ends, and with domain reload disabled the
`[InitializeOnLoad]` constructor never runs again to restore them. Console capture therefore died silently
after the first ClientSim scenario and stayed dead. `LogCapture` now re-subscribes on
`playModeStateChanged`, and `tests/m3-verify.mjs` guards it.

## 12. Environmental failures that look like content failures

Two cases where the SDK reports a content problem for an environment cause. Both now have up-front fixes,
because an agent cannot debug what the error message actively misdirects it about.

**"Scene export failed. Check console output for details."** `VRCWorldAssetExporter` stages the bundle under
`%TEMP%\<companyName>\<productName>` and begins by `File.Delete`-ing a stale file there. When temp cleanup has
removed that folder the delete throws `DirectoryNotFoundException` and the build dies — with nothing in the
project to explain it. The same scene built fine minutes earlier. `verify.build_local` recreates the folder
before every build.

**Three identically-named types.** The UdonSharp compiler Roslyn-compiles user scripts into in-memory
`UdonSharpRoslynCompileAssembly{N}` assemblies, each carrying a *shadow copy* of every `UdonSharpBehaviour`. A
domain-wide `GetType` finds the same class three times with three different module ids, and attaching a shadow
produces a component `GetComponent` can never find again. Type resolution goes through the program asset's
`GetClass()`, which is authoritative.

## 13. Size limits are the SDK's to state

`budget.report` calls `VRC.ValidationHelpers.GetAssetBundleSizeLimit` rather than hardcoding numbers, and the
answer corrects a common belief: the SDK returns a real limit for **mobile** and `-1` for **PC**. The widely
quoted 100MB world limit is the mobile one; on PC, bundle size is a performance judgement rather than a gate.
A hardcoded 100MB would have reported every large PC world as over limit.

The same principle applies to the Udon exposure whitelist and the syncable-type list: both are read from the
installed SDK (`UdonEditorManager.GetNodeDefinitions`, `UdonNetworkTypes.CanSync`) rather than transcribed.
`tests/m5-lint-whitelist.mjs` cross-checks the linter's own hardcoded sync table against the SDK, so the two
cannot quietly disagree.

## 14. Geometry checks have to earn their keep

`scene.spatial_audit` exists because an agent placing objects by coordinate has no feel for whether a player
can stand on the floor. Three false-positive classes had to be eliminated before it was worth reading:

- **Floors.** The lowest geometry in a scene has nothing beneath it by definition. Ground-level and
  broad-and-thin geometry is exempt.
- **Probe rays starting inside the support.** Casting from just *below* an object put the origin inside the
  floor, and Unity does not report a collider a ray starts inside — so every wall in a generated room looked
  unsupported. The probe now starts just inside the object's own underside and samples the corners too, so an
  object resting on a narrow beam is not flagged.
- **Things held from the side.** A doorway lintel, a beam, a wall sign. The check now tests lateral contact
  before flagging.

`tests/m2-world.mjs` still asserts a genuinely floating object *is* flagged, so narrowing the false positives
did not amount to disabling the check.

## 15. Checkpointing

A Unity scene is a huge generated YAML document: it cannot be merged, cannot be reliably hand-reverted, and
Unity's undo stack does not survive a domain reload. A commit before each risky step is the only workable undo.

`project_rollback` is destructive to the working tree but not to work. It commits the current state first, and
then anchors the pre-rollback head under `refs/agentic/safety/<timestamp>`. The ref matters: `reset --hard`
leaves those commits unreachable from any ref, where `git gc` is entitled to prune them — a safety net that can
be garbage collected is not a safety net.

`.gitignore` handling recognises Unity's official template (`/[Ll]ibrary/`) rather than comparing literally,
which would have reported every correctly-configured project as broken and then appended duplicate rules.
