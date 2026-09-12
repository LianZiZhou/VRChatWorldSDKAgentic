# Runbook

Operating this unattended, and what to do when something stops. Everything here has happened at least once
on a real machine; nothing is hypothetical.

## Starting from nothing

```bash
node scripts/install.mjs --project "E:/Unity/WorldTest"
node bundled/vrcw-supervisor.mjs editors --project "E:/Unity/WorldTest"
node bundled/vrcw-supervisor.mjs watch   --project "E:/Unity/WorldTest" --auto-dismiss-dialogs true
node tests/run-all.mjs --quick
```

`editors` before `watch`: several Unity installs can claim the same version string, and only one of them is
the build VRChat supports. Check its verdict rather than trusting a folder name.

Sign in to VRChat once in that project's SDK control panel. Nothing else needs a human.

## After a reboot

One command. The supervisor picks the right editor and launches it; the bridge rebinds a fresh port and
republishes its registry entry; the bundled generic package starts its own bridge from the editor's load
hook. Nothing needs reinstalling.

```bash
node bundled/vrcw-supervisor.mjs watch --project "E:/Unity/WorldTest" --auto-dismiss-dialogs true
```

Two files left over from the previous boot are worth knowing about, because both look like health and are
not:

- `%LOCALAPPDATA%\AgenticVrcw\instances\*.json` names a port and a pid that no longer exist. Clients check
  process liveness before believing it.
- `~/.unity-mcp/unity-mcp-status-*.json` names the companion's port. That file survives a crash or a reboot
  and is only rewritten on a state change, so neither its presence nor its timestamp means anything.
  `companion_status` connects to the port rather than trusting the file, and reports `serving`.

If the generic tools answer "No Unity Editor instances found" against an editor that is plainly running, that
stale file is the reason and `companion_setup` is the fix.

## Is it healthy?

| Question | Call |
|---|---|
| Is the editor alive and pumping? | `unity_ping`, `unity_health` |
| What is it doing right now? | `unity_editor_state`, `unity_compile_status`, `unity_console` |
| Can it build at all? | `world_project_status` (layers, collision matrix, login state) |
| Is the generic half available? | `companion_status` |
| What does the supervisor think? | `<project>/.agentic/supervisor.json` |

`unity_health` is the one to read first. `secondsSincePump` above a few seconds means the main thread is busy
or blocked; `queueDepth` climbing means work is arriving faster than the editor can retire it.

## Symptoms

### Every call returns `EDITOR_BUSY`, but `unity_ping` works

The main-thread pump has stopped while the process is fine. Almost always a modal dialog.

```bash
node bundled/vrcw-supervisor.mjs dialogs --project "E:/Unity/WorldTest"
node bundled/vrcw-supervisor.mjs dismiss --project "E:/Unity/WorldTest"
```

`dialogs` separates real dialog boxes from Unity's floating panels — the VRChat SDK control panel is a
floating panel and never blocks anything, so it is listed under `otherWindows`. `dismiss` answers only
allowlisted prompts; anything else it reports and leaves alone, because pressing the default button of an
unrecognised dialog is a way to agree to something nobody asked for.

Prevention: `unity_harden_editor` once after the editor starts, and run the supervisor with
`--auto-dismiss-dialogs true` during anything that rewrites assets.

### Nothing answers, but Unity is in the process list

Check the registry entry and the log:

```bash
cat "$LOCALAPPDATA/AgenticVrcw/instances/"*.json
tail -40 "E:/Unity/WorldTest/.agentic/unity-editor.log"
```

A repeating `Bridge failed to start` line means the bridge could not bind. It now falls back to a different
port and republishes the registry entry, so clients follow it automatically — if you see that line repeating
without recovery on a current build, it is a bug worth reporting rather than a state to wait out.

The supervisor restarts a *dead* editor on its own. It restarts a *wedged* one only with
`--allow-wedge-restart true`, because a wedged editor may be holding unsaved work and a restart throws it
away. Decide deliberately.

### A tool reports the project is not set up

```
world_fix_project          layers, the collision matrix, player settings
world_scaffold             a scene the SDK will accept
world_validate             the SDK's own validations, as data
```

`world_fix_project` calls the SDK's setup routines directly, bypassing the confirmation dialogs their GUI
wrappers raise. That is the only reason it exists.

### The generic Unity tools are missing, or report no editor

```
companion_status           names the cause; nextSteps says what to do
companion_setup            fixes it, idempotently
```

Three causes, and `companion_status` says which. Unity rescans `Packages/` at startup only, so a package added
to a running editor never compiles until `PackageManager.Client.Resolve()` runs. The companion's bridge does
not survive a domain reload, so something has to restart it — our load hook does, every load. And a status
file left behind by a previous editor advertises a port nothing is listening on, which is why
`companion_status` reports `serving` from a connection attempt rather than from the file. A freshly opened
editor needs nothing.

### UdonSharp fields are empty after a build

Fields written to the proxy must be copied to the backing UdonBehaviour, or they are silently lost. Use
`udon_set_fields`, which does that; do not set UdonSharp fields through generic component editing.

### A build fails with "Scene export failed"

Environmental, not content: the SDK stages through `%TEMP%\<company>\<product>` and dies if temp cleanup
removed it. `verify_build_local` recreates it before every build, so this should not recur — if it does, look
for a process deleting temp folders while the build runs.

### The linter is unavailable

`udon_lint` builds the analyzer on first use if `dotnet` is on PATH. Without dotnet, everything else works;
you lose the fast feedback loop and pay a Unity round trip per mistake instead.

## Before anything risky

```
project_checkpoint --label "before <whatever>"
```

A Unity scene is a large generated YAML file. It cannot be hand-merged and cannot be reliably hand-reverted,
so checkpoint and roll back instead of editing your way out. `project_rollback` commits the current state
first under `refs/agentic/safety/`, so the rollback itself is reversible.

After a rollback the editor still holds the old scene in memory. Reopen it, or call `unity_refresh_assets`,
before trusting what the editor shows.

## What is deliberately not here

Upload. The autonomy boundary is local Build & Test: `BuildAndUpload` and the publish APIs are not exposed,
so no sequence of tool calls can publish a world. Raising that boundary is a decision with consequences
outside this machine, and it should be taken explicitly rather than inherited from a config file.

`verify_multiclient` launches real VRChat clients and has never been run end to end here. Its preflight is
tested; the launch path is not. Treat it as untested code.
