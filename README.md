# VRChatWorldSDKAgentic

Tooling that lets an AI agent develop VRChat worlds autonomously in **Unity 2022.3.22f1 + VRChat Worlds
SDK 3.10.5**.

| Package | What it is |
|---|---|
| `packages/unity-bridge` | UPM package `com.agentic.vrcw` — an always-on HTTP bridge inside the Unity Editor |
| `packages/vrcw-mcp` | Node/TS MCP server that fronts the bridge |
| `packages/supervisor` | Owns the Unity Editor process: picks the right install, relaunches on death, reports stalls |
| `packages/udon-analyzer` | *(M1)* Roslyn analyzer for UdonSharp constraints — runs outside Unity, sub-second |

Why a custom bridge at all: VRChat's build API (`IVRCSdkWorldBuilderApi`) needs a live SDK control-panel
EditorWindow and a pumped `EditorApplication.update`, so `-batchmode -executeMethod` cannot drive it. And
Unity's own MCP requires Unity 6, which VRChat does not support. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Status

**M0 complete and verified against a live editor** (`20/20` in `tests/m0-smoke.mjs`), including recovery
from an unplanned editor kill.

Working today:

| Tool | Purpose |
|---|---|
| `unity_instances`, `unity_ping`, `unity_health`, `unity_methods` | discovery and liveness |
| `unity_editor_state` | scenes, build target, and the VRChat preconditions (SDK panel open? logged in?) |
| `unity_console` | sequence-numbered console stream that survives domain reloads |
| `unity_compile_status` | structured C# diagnostics |
| `unity_eval_csharp` | compile + run arbitrary editor C#, with captured `Debug.Log` output |
| `unity_invoke_static`, `unity_get_static` | reflection calls that need no compiler and reach `internal` SDK members |
| `unity_menu_item`, `unity_refresh_assets` | editor actions |
| `unity_job_submit/status/list/cancel/wait` | long-running work as pollable jobs |

Not yet built: everything VRChat-authoring (M1–M5) — UdonSharp compile/attach/lint, scene scaffolding and
validation, budget checks, ClientSim verification, asset pipeline.

## Setup

```bash
npm --prefix packages/vrcw-mcp install && npm --prefix packages/vrcw-mcp run build
npm --prefix packages/supervisor install && npm --prefix packages/supervisor run build
```

Install the bridge into a VRChat project (a junction keeps edits live):

```bash
cmd //c mklink //J "E:\Unity\WorldTest\Packages\com.agentic.vrcw" "D:\Projects\VRChatWorldSDKAgentic\packages\unity-bridge"
```

Check which Unity installs may open the project, then launch:

```bash
node packages/supervisor/dist/index.js editors --project "E:\Unity\WorldTest"
node packages/supervisor/dist/index.js watch   --project "E:\Unity\WorldTest"
```

`editors` exists because several installs can claim the same version string while only one is the build
VRChat supports — the China (`...f1c1`) builds are rejected. See ARCHITECTURE §6.

The MCP server is wired in `.mcp.json` (absolute path; edit it if you move the repo).

## Verify

```bash
node tests/m0-smoke.mjs
```

Requires an editor already open on a project with the bridge installed. The test gates on editor idleness
first, because writing scripts triggers a recompile and domain reload during which calls fail at the
transport level.

## One-time human step

Sign in once in the SDK control panel of that Unity project. The panel only registers builders while
`APIUser.IsLoggedIn` (`VRCSdkControlPanelBuilder.cs:919`), so even local Build & Test needs it. Credentials
land in `EditorPrefs` via `SecurePlayerPrefs`; the agent never handles a password or 2FA code.

## Layout

- `generated/` — indexes generated from the *installed* SDK (Udon whitelist, component whitelist, events)
- `scenarios/` — declarative world-verification scenarios (YAML)
- `skills/` — agent knowledge packs
- `tests/` — acceptance tests that drive a live editor

## Notes

- The bridge can compile and run arbitrary C# in the editor. It binds `127.0.0.1` only and requires a
  per-session token; treat that token as a local credential.
- `<project>/.agentic/` holds bridge state: console JSONL, compile diagnostics, job journal, supervisor
  status, and the editor log.
