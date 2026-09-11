# VRChatWorldSDKAgentic

Tooling that lets an AI agent develop VRChat worlds autonomously in **Unity 2022.3.22f1 + VRChat Worlds
SDK 3.10.5** — organising assets, writing Udon, assembling scenes, verifying behaviour, and building.

Clone it, run one command, and both halves of the MCP surface come up against your project:

```bash
node scripts/install.mjs --project "E:/Unity/WorldTest"
```

No `npm install`, no second repository to fetch, no editor preferences to set by hand. The generic Unity MCP
layer ships in the box.

| Package | What it is |
|---|---|
| `packages/unity-bridge` | UPM package `com.agentic.vrcw` — an always-on HTTP bridge inside the Unity Editor |
| `packages/vrcw-mcp` | Node/TS MCP server: 71 tools fronting the bridge |
| `packages/supervisor` | Owns the editor process: picks the right install, relaunches on death, names and answers blocking dialogs |
| `packages/udon-analyzer` | Roslyn linter for UdonSharp — runs outside Unity, ~150ms |
| `third_party/unity-mcp` | **Bundled** generic Unity MCP (CoplayDev, MIT, pinned tag) — GI baking, NavMesh, prefabs, Test Runner, profiling |
| `bundled/` | Committed single-file builds of the two Node programs, dependencies inlined |

Two servers, one editor. Ours covers everything VRChat-specific; the bundled one covers the generic Unity
surface we deliberately do not reimplement. They share nothing but the editor they attach to.

Why a custom bridge at all: VRChat's build API needs a live SDK control-panel EditorWindow and a pumped
`EditorApplication.update`, so `-batchmode -executeMethod` cannot drive it — and Unity's own MCP requires
Unity 6, which VRChat does not support. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Status

**Complete and verified against a live editor: 165 checks across 12 suites.**

| Suite | Covers | Result |
|---|---|---|
| `tests/e2e-world.mjs` | **scaffold → script → verify → build the full world, PC and Quest** | 20/20 in ~48s |
| `tests/m0-smoke.mjs` | bridge transport, eval, reflection, jobs | 21/21 |
| `tests/m1-udon.mjs` | UdonSharp authoring loop | 15/15 |
| `tests/m2-world.mjs` | project setup, scaffolding, validation, scene spec, audit, renders | 20/20 |
| `tests/m3-verify.mjs` | ClientSim behaviour, local builds, real-client preflight | 16/16 |
| `tests/m4-budget-assets.mjs` | dual-platform budget, provenance, procedural geometry | 17/17 |
| `tests/m5-lint-whitelist.mjs` | out-of-Unity linter, Udon exposure whitelist | 11/11 |
| `tests/m5-checkpoint.mjs` | checkpoint / rollback safety | 10/10 |
| `tests/companion-mcp.mjs` | bundled generic layer: licence, install, telemetry, reachability | 13/13 |
| `tests/mcp-handshake.mjs` | MCP stdio protocol and tool surface | 6/6 |
| `tests/m4-open-assets.mjs` | open-licence fetching, against the live Poly Haven API | 8/8 |
| `tests/dialog-recovery.mjs` | raises a real modal dialog and recovers from it | 8/8 |

```bash
node tests/run-all.mjs
```

The end-to-end run builds the world the plan called for: **a button that opens a door, a counter
synchronised across players, a video player, a prop you can pick up, and a chair you can sit in**. It
scaffolds the scene, generates a room with a doorway, places every object and SDK component declaratively,
lints four UdonSharp behaviours before Unity sees them, compiles them, wires and verifies their fields reach
the backing UdonBehaviours, audits the geometry, renders the scene, checks the budget, proves every
interaction in a ClientSim play-mode session — interact, grab, drop, sit, stand, and the video callbacks, 46
scenario steps — parses the telemetry each feature emits, and produces a real `.vrcw` for **both PC and
Quest**, with no human in the editor.

One tool is **not** end-to-end verified: `verify_multiclient`, which launches real VRChat clients to test
synchronisation, needs a VRChat client installed and an account signed in. Its preconditions, log parsing and
preflight are tested (`verify_multiclient_preflight`); the launch-and-converge path has never been run here.
It reports precisely which preconditions are missing rather than failing opaquely, but treat it as untested
code until you have run it yourself.

Two other honest limits. A job interrupted by an editor restart is journalled and reported as `interrupted`;
it does not resume by itself, because a half-finished build or play-mode session has to start again anyway.
And scenario steps do not check reachability — `grab` and `interact` reach the object wherever it is, so
whether a player could actually get to it stays `scene_spatial_audit`'s question.

## Tools

**Bridge and observation** — `unity_instances`, `unity_ping`, `unity_health`, `unity_methods`,
`unity_harden_editor`, `unity_editor_state`, `unity_console`, `unity_compile_status`, `unity_refresh_assets`,
`unity_menu_item`, `unity_eval_csharp`, `unity_invoke_static`, `unity_get_static`,
`unity_job_submit/status/list/cancel/wait`

**Bundled generic layer** — `companion_status`, `companion_setup` (diagnose and repair it from inside an
agent run; its own ~47 tools arrive through the second MCP server)

**UdonSharp** — `udon_lint` (no Unity), `udon_whitelist_query`, `udon_syncable`, `udon_export_whitelist`,
`udon_status`, `udon_create_script`, `udon_compile`, `udon_attach`, `udon_set_fields`, `udon_read_vars`,
`udon_list_behaviours`

**World and scene** — `world_project_status`, `world_fix_project`, `world_scaffold`, `world_describe_scene`,
`world_validate`, `scene_apply_spec`, `scene_describe`, `scene_component_whitelist`, `scene_spatial_audit`,
`scene_survey_shots`, `geo_generate`, `geo_kinds`

**Verification and build** — `verify_scenario_schema`, `verify_clientsim` (18 scenario step types: wait,
interact, send_event, set_var, grab, drop, sit, stand, teleport, respawn, and assertions on variables, active
state, position, held state, station occupancy and the console), `verify_build_local`,
`verify_multiclient_preflight`, `verify_multiclient`, `verify_vrc_logs`, `verify_udon_exceptions`,
`world_build_status`, `world_set_dry_run`, `world_switch_platform`

**Budget and assets** — `budget_report`, `budget_snapshot`, `budget_diff`, `budget_import_audit`,
`budget_apply_import_presets`, `assets_inventory`, `assets_unused`, `assets_make_material`,
`assets_import_file`, `assets_ledger`, `open_assets_sources`, `open_assets_search`,
`open_assets_files`, `open_assets_fetch`

**Safety** — `project_status`, `project_checkpoint`, `project_checkpoints`, `project_rollback`

Upload is deliberately absent. The autonomy boundary is local Build & Test, so `BuildAndUpload` and the
publish APIs are not exposed at all rather than merely discouraged.

## Install

```bash
node scripts/install.mjs --project "E:/Unity/WorldTest"
```

It links `com.agentic.vrcw` and the bundled `com.coplaydev.unity-mcp` into the project, checks the committed
bundles, builds the linter if `dotnet` is present, runs `uv sync` for the bundled Python server, and writes
`.mcp.json` with both servers — the generic one with telemetry switched off. Every step is idempotent and
reports itself, so re-running it is also how you diagnose a setup.

| Option | Effect |
|---|---|
| `--copy` | copy the Unity packages instead of linking them (a link keeps your edits live) |
| `--mcp-config <file>` | write the MCP config somewhere else; `--no-mcp-config` leaves it alone |
| `--skip-server-deps` | do not run `uv sync` |
| `--skip-build` | use only what is committed |
| `--json` | machine-readable report |

Then check which Unity installs may open the project, and run the supervisor:

```bash
node bundled/vrcw-supervisor.mjs editors --project "E:/Unity/WorldTest"
node bundled/vrcw-supervisor.mjs watch --project "E:/Unity/WorldTest" --auto-dismiss-dialogs true
```

`editors` exists because several installs can claim the same version string while only one is the build
VRChat supports — the Unity China (`...f1c1`) builds are rejected. `--auto-dismiss-dialogs` lets the
supervisor answer allowlisted blocking prompts; without it a modal dialog stalls an unattended run
indefinitely.

### One-time human step

Sign in once in the SDK control panel of that Unity project. The panel only registers builders while
`APIUser.IsLoggedIn`, so even local Build & Test needs it. Credentials land in `EditorPrefs` via
`SecurePlayerPrefs`; the agent never handles a password or 2FA code.

### If the generic half goes quiet

`companion_status` says why, in terms of the three things that actually go wrong: Unity only rescans
`Packages/` at startup, so a package added to a running editor never compiles until
`PackageManager.Client.Resolve()` runs; the companion's bridge is gated behind an EditorPref that defaults to
false; and that bridge does not survive a domain reload, so something has to start it again after every
recompile. `companion_setup` fixes the first two on demand, and the bridge handles the third on every load —
which is why a freshly opened editor needs nothing.

## Development

```bash
npm install            # only for building; a clone runs from bundled/ without it
npm run build          # packages/*/dist
npm run bundle         # refresh the committed bundled/ artifacts
npm test               # every suite
```

Distribution, when you want it elsewhere:

```bash
node scripts/make-vpm-listing.mjs --base-url https://your.host/vpm   # Creator Companion listing + zip
node scripts/publish.mjs --dry-run                                   # npm, dry by default
node scripts/update-vendored.mjs --tag v10.3.0                       # refresh the bundled generic layer
```

`update-vendored.mjs` refuses to vendor a tag that is not still under a licence permitting redistribution,
and `tests/companion-mcp.mjs` fails if the upstream tools we rely on are renamed.

## Layout

- `skills/` — agent knowledge packs (`vrcw-udon`, `vrcw-world`), grounded in the SDK as verified here
- `generated/` — indexes generated from the *installed* SDK, so they cannot drift (`udon-whitelist.json`:
  35,333 nodes, 133 events, 783 types)
- `tests/` — acceptance suites that drive a live editor
- `scripts/` — install, bundle, vendor refresh, distribution
- `third_party/unity-mcp/VENDORED.md` — what is vendored, from which tag, under which licence, and why
- `docs/ARCHITECTURE.md` — why the design is shaped this way, and the SDK constraints behind each decision

## Notes

- The bridge compiles and runs arbitrary C# in the editor. It binds `127.0.0.1` only and requires a
  per-session token; treat that token as a local credential.
- `<project>/.agentic/` holds bridge state: console JSONL, compile diagnostics, job journal, budget snapshots,
  asset ledger, renders, supervisor status, editor log.
- Regenerate the Udon whitelist after an SDK upgrade: `udon_export_whitelist`.
- The bundled generic package also starts a server of its own on load (`127.0.0.1:8080` by default) — that is
  its behaviour, not ours; the telemetry opt-out covers it because the editor passes it down.
- This repository has no licence file yet. The vendored third-party code keeps its own (MIT); decide what you
  want for the rest before publishing anything from `scripts/publish.mjs`.
