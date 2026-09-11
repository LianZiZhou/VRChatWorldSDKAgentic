# VRChatWorldSDKAgentic

Tooling that lets an AI agent develop VRChat worlds autonomously in **Unity 2022.3.22f1 + VRChat Worlds
SDK 3.10.5** — organising assets, writing Udon, assembling scenes, verifying behaviour, and building.

| Package | What it is |
|---|---|
| `packages/unity-bridge` | UPM package `com.agentic.vrcw` — an always-on HTTP bridge inside the Unity Editor |
| `packages/vrcw-mcp` | Node/TS MCP server: 61 tools fronting the bridge |
| `packages/supervisor` | Owns the editor process: picks the right install, relaunches on death, names and answers blocking dialogs |
| `packages/udon-analyzer` | Roslyn linter for UdonSharp — runs outside Unity, ~150ms |

Why a custom bridge at all: VRChat's build API needs a live SDK control-panel EditorWindow and a pumped
`EditorApplication.update`, so `-batchmode -executeMethod` cannot drive it — and Unity's own MCP requires
Unity 6, which VRChat does not support. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Status

**Complete and verified against a live editor: 141 checks across 10 suites.**

| Suite | Covers | Result |
|---|---|---|
| `tests/e2e-world.mjs` | **scaffold → script → verify → build a real world** | 18/18 in ~20s |
| `tests/m0-smoke.mjs` | bridge transport, eval, reflection, jobs | 21/21 |
| `tests/m1-udon.mjs` | UdonSharp authoring loop | 15/15 |
| `tests/m2-world.mjs` | project setup, scaffolding, validation, scene spec, audit, renders | 20/20 |
| `tests/m3-verify.mjs` | ClientSim behaviour, local builds, real-client preflight | 16/16 |
| `tests/m4-budget-assets.mjs` | dual-platform budget, provenance, procedural geometry | 17/17 |
| `tests/m5-lint-whitelist.mjs` | out-of-Unity linter, Udon exposure whitelist | 11/11 |
| `tests/m5-checkpoint.mjs` | checkpoint / rollback safety | 10/10 |
| `tests/mcp-handshake.mjs` | MCP stdio protocol and tool surface | 6/6 |
| `tests/dialog-recovery.mjs` | raises a real modal dialog and recovers from it | 7/7 |

The end-to-end run scaffolds a world, generates a room with a doorway, writes and compiles an UdonSharp
counter, wires and verifies its fields reach the backing UdonBehaviour, audits the geometry, renders the
scene, checks the budget for PC and Quest, proves the behaviour in a ClientSim play-mode session, parses its
telemetry, and produces a real `.vrcw` — with no human in the editor.

## Tools

**Bridge and observation** — `unity_instances`, `unity_ping`, `unity_health`, `unity_methods`,
`unity_harden_editor`, `unity_editor_state`, `unity_console`, `unity_compile_status`, `unity_refresh_assets`,
`unity_menu_item`, `unity_eval_csharp`, `unity_invoke_static`, `unity_get_static`,
`unity_job_submit/status/list/cancel/wait`

**UdonSharp** — `udon_lint` (no Unity), `udon_whitelist_query`, `udon_syncable`, `udon_export_whitelist`,
`udon_status`, `udon_create_script`, `udon_compile`, `udon_attach`, `udon_set_fields`, `udon_read_vars`,
`udon_list_behaviours`

**World and scene** — `world_project_status`, `world_fix_project`, `world_scaffold`, `world_describe_scene`,
`world_validate`, `scene_apply_spec`, `scene_describe`, `scene_component_whitelist`, `scene_spatial_audit`,
`scene_survey_shots`, `geo_generate`, `geo_kinds`

**Verification and build** — `verify_scenario_schema`, `verify_clientsim`, `verify_build_local`,
`verify_multiclient_preflight`, `verify_multiclient`, `verify_vrc_logs`, `verify_udon_exceptions`,
`world_build_status`, `world_set_dry_run`, `world_switch_platform`

**Budget and assets** — `budget_report`, `budget_snapshot`, `budget_diff`, `budget_import_audit`,
`budget_apply_import_presets`, `assets_inventory`, `assets_unused`, `assets_make_material`,
`assets_import_file`, `assets_ledger`

**Safety** — `project_status`, `project_checkpoint`, `project_checkpoints`, `project_rollback`

Upload is deliberately absent. The autonomy boundary is local Build & Test, so `BuildAndUpload` and the
publish APIs are not exposed at all rather than merely discouraged.

## Setup

```bash
npm --prefix packages/vrcw-mcp install && npm --prefix packages/vrcw-mcp run build
npm --prefix packages/supervisor install && npm --prefix packages/supervisor run build
dotnet build -c Release packages/udon-analyzer/UdonAnalyzer.csproj
```

Install the bridge into a VRChat project (a junction keeps edits live):

```bash
cmd //c mklink //J "E:\Unity\WorldTest\Packages\com.agentic.vrcw" "D:\Projects\VRChatWorldSDKAgentic\packages\unity-bridge"
```

Check which Unity installs may open the project, then run the supervisor:

```bash
node packages/supervisor/dist/index.js editors --project "E:\Unity\WorldTest"
node packages/supervisor/dist/index.js watch   --project "E:\Unity\WorldTest" --auto-dismiss-dialogs true
```

`editors` exists because several installs can claim the same version string while only one is the build VRChat
supports — the Unity China (`...f1c1`) builds are rejected. `--auto-dismiss-dialogs` lets the supervisor answer
allowlisted blocking prompts; without it a modal dialog stalls an unattended run indefinitely.

The MCP server is wired in `.mcp.json` (absolute path; edit it if you move the repo).

## Verify

```bash
node tests/e2e-world.mjs
```

Requires an editor already open on a project with the bridge installed. Every suite gates on editor idleness
first, because writing scripts triggers a recompile and domain reload during which calls fail at the transport
level.

## One-time human step

Sign in once in the SDK control panel of that Unity project. The panel only registers builders while
`APIUser.IsLoggedIn`, so even local Build & Test needs it. Credentials land in `EditorPrefs` via
`SecurePlayerPrefs`; the agent never handles a password or 2FA code.

`verify_multiclient` additionally needs a VRChat client installed — it reports precisely which preconditions
are missing rather than failing opaquely.

## Layout

- `skills/` — agent knowledge packs (`vrcw-udon`, `vrcw-world`), grounded in the SDK as verified here
- `generated/` — indexes generated from the *installed* SDK, so they cannot drift (`udon-whitelist.json`:
  35,333 nodes, 133 events, 783 types)
- `tests/` — acceptance suites that drive a live editor
- `docs/ARCHITECTURE.md` — why the design is shaped this way, and the SDK constraints behind each decision

## Notes

- The bridge compiles and runs arbitrary C# in the editor. It binds `127.0.0.1` only and requires a
  per-session token; treat that token as a local credential.
- `<project>/.agentic/` holds bridge state: console JSONL, compile diagnostics, job journal, budget snapshots,
  asset ledger, renders, supervisor status, editor log.
- Regenerate the Udon whitelist after an SDK upgrade: `udon_export_whitelist`.
