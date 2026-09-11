# Vendored: MCP for Unity (CoplayDev)

This directory is a **verbatim copy** of third-party code, included so the toolchain works out of the box
without asking anyone to install a second project by hand.

| | |
|---|---|
| Upstream | https://github.com/CoplayDev/unity-mcp |
| Pinned at | tag **v10.2.0** (Unity package `com.coplaydev.unity-mcp` 10.2.0, server `mcpforunityserver` 10.2.0) |
| Licence | MIT — Copyright (c) 2025 CoplayDev. Full text in `LICENSE`. |
| Modified? | **No.** Nothing here is patched; all configuration is applied from outside via environment variables and the installer. |

## Why it is here

Our own bridge (`com.agentic.vrcw`) covers everything VRChat-specific: Udon, the SDK build pipeline,
ClientSim verification, world budgets. It deliberately does not reimplement the generic Unity surface —
lightmap and GI baking, NavMesh, prefab operations, the Test Runner, profiling, animation, VFX. This package
provides that half.

The two run side by side: separate Unity packages, separate servers, separate ports. They share only the
editor they are attached to.

## What was left out

`website/`, `docs/`, `TestProjects/`, `.github/` and the Docker files are omitted — they are development and
publishing material for the upstream project, not runtime requirements. `MCPForUnity/`, `Server/`,
`CustomTools/`, `LICENSE`, `README.md` and `manifest.json` are kept intact.

`*.meta` files are committed here on purpose, unlike in our own package. Unity assets reference each other by
GUID, and those GUIDs live in the `.meta` files; dropping them would break the package's internal references.

## Telemetry is disabled by default

The upstream server reports usage to `https://api-prod.coplay.dev/telemetry/events`, and it is **opt-out** —
enabled unless told otherwise. Here it is off, and that takes two separate measures because there are two
halves and, in practice, two server processes:

| What | How it is switched off | Where that happens |
|---|---|---|
| The editor-side reporter | EditorPref `MCPForUnity.TelemetryDisabled` | the bridge, once per project |
| The server an MCP client launches | `DISABLE_TELEMETRY=1` in its `.mcp.json` entry | `scripts/install.mjs` |
| The server the Unity package launches itself on load | `DISABLE_TELEMETRY=1` set inside the editor process, which the child inherits | the bridge, on every load |

That third row is the one worth knowing about. The Python server reads its opt-out from environment
variables **only** — it has no persisted setting — and the server the Unity package starts for itself
inherits the editor's environment, not anything an MCP client configures. Setting the variable in the editor
process is what covers it.

`tests/companion-mcp.mjs` asserts all three, and also asks the running server to confirm it for itself via
`manage_editor {action: "telemetry_status"}`.

This is a deliberate change of default, not a judgement about the upstream project: a toolchain that an agent
runs unattended should not start sending data to a third party because nobody thought to look. To re-enable
it, remove those environment variables and set the EditorPref back to false.

## Nothing here is patched

The two things this package needs before an agent can use it are done from outside, by
`Packages/com.agentic.vrcw/Editor/Bridge/CompanionSetup.cs`:

1. `UnityEditor.PackageManager.Client.Resolve()`, because Unity only rescans `Packages/` at startup — a
   package added to a running editor never compiles without it, and an `AssetDatabase.Refresh()` is not
   enough.
2. `EditorPrefs.SetBool("MCPForUnity.AutoStartOnLoad", true)`, because that pref defaults to false and its
   server otherwise reports "No Unity Editor instances found" against a healthy editor.

Both are reflective and optional: our bridge compiles and runs in a project that does not have this package
at all.

## Updating

```bash
node scripts/update-vendored.mjs --tag v10.3.0
```

That refetches the pinned tag, replaces this tree, and preserves `VENDORED.md`. Re-run the installer
afterwards so the Unity project picks up the new package, and re-run the test suites — an upstream change can
alter tool names that our documentation references.

## Attribution

MCP for Unity is by Coplay (https://www.coplay.dev) and contributors, including Marcus Sanatan, David Sarno
and Wu Shutong. It is redistributed here under the MIT licence, whose terms require this notice to travel with
the code.
