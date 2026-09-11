---
name: vrcw-world
description: Build, audit and ship VRChat world scenes for PC and Quest. Use when scaffolding a world, assembling a scene, checking performance budgets, organising assets, or producing a build.
---

# VRChat world building

Verified against **VRChat Worlds SDK 3.10.5 / Unity 2022.3.22f1**.

## Before anything else

```
project_checkpoint        commit first — a Unity scene cannot be hand-merged or reliably hand-reverted
unity_harden_editor       remove the editor's modal prompts
world_fix_project         layers + collision matrix, or the SDK refuses to build at all
```

**Why hardening matters more than it sounds.** A modal dialog halts Unity's update loop, which halts the
bridge's pump, so every call returns `EDITOR_BUSY` and an unattended run stops dead with nobody to click the
button. Two prompts do this routinely: "detected external changes, reload?" (exists only when Asset Pipeline
Auto Refresh is off — hardening turns it on) and "the open scene(s) have been modified externally" (raised by
rewriting a scene file under a running editor, which is what a rollback does). Run the supervisor with
`--auto-dismiss-dialogs true` during anything that rewrites assets.

## A scene the SDK will accept

`world_scaffold` produces all of it; `world_describe_scene` reports a `problems` list when something is off.
The hard requirements:

- exactly **one** `VRCSceneDescriptor` — more is a build-blocking error
- a `PipelineManager` on the **same GameObject** as the descriptor
- at least one non-null spawn point — `WORLD_SPAWN_EMPTY_ERROR` otherwise
- layers and the collision matrix configured project-wide

## Assembling the scene

Prefer **`scene_apply_spec`** over placing objects one at a time: it is one call, idempotent (keyed on
hierarchy path, so re-applying converges), and it warns about components VRChat strips.

**Components not on VRChat's whitelist are deleted from the built world, silently.** Check with
`scene_component_whitelist`. `UnityEngine.Animation` (legacy) is not whitelisted, for example. A scene can
look correct in the editor and lose behaviour once uploaded.

Never add an `UdonSharpBehaviour` through `scene_apply_spec` — it refuses, because a plain `AddComponent`
skips the backing-UdonBehaviour setup and produces a component that never runs. Use `udon_attach`.

**`geo_generate`** builds rooms, corridors and stairs. Its value is the arithmetic: a doorway is a wall split
into side segments plus a lintel, and one wrong offset leaves a gap a player walks through. `size` is the
room's **interior**; the floor is extended to pass under the walls.

## Checking your work

You cannot see the world. Use both tools, because they answer different questions:

- **`scene_spatial_audit`** — geometry, as numbers. Is the spawn standing on something? What fraction of the
  scene has ground beneath it? What floats, interpenetrates, lacks a collider, or has degenerate scale?
  Findings are prompts to look, not verdicts: a deliberately floating lamp is not a bug.
- **`scene_survey_shots`** — actual renders from the spawn at eye height, an orbit, and top-down. The only way
  to judge composition, facing and whether the lighting came out black.

## Budget, and the PC/Quest split

`budget_report` reads VRChat's limits from the SDK rather than from a constant. That reveals something worth
knowing: **VRChat enforces a bundle size limit for mobile but not for PC** — the widely quoted 100MB figure is
the mobile one. On PC, size is a performance judgement; on Android it is a hard gate.

In the report, **errors are things VRChat blocks** and warnings are recommendations. Worlds have no hard
polygon or material limit.

For a dual-platform world, import settings decide the outcome:

```
budget_import_audit  --platform Android     # see what would change
budget_apply_import_presets --platform Android
```

- **Android textures must be ASTC.** The SDK's project check reports an *error* for anything else and refuses
  the build. Target 1K maximum.
- Long audio clips left on `DecompressOnLoad` will exhaust Quest's memory — stream them.
- Realtime lights, multiple mirrors and many reflection probes are each expensive enough on Quest to matter.

Switching platform with `world_switch_platform` reloads the script domain, so any job in flight is interrupted
and must be resubmitted. Build one platform at a time.

## Assets

`assets_inventory` shows what is consuming the project. `assets_unused` reports what no scene can reach and
how much it would free — the easiest megabytes to recover. It deliberately never condemns scripts, which are
reached through code rather than references.

`assets_import_file` **requires** `source` and `licence`. An agent importing assets without recording
provenance produces a world nobody can later clear for distribution, and that cannot be reconstructed
afterwards. `assets_ledger --writeAttribution` regenerates `ATTRIBUTION.md` from those records.

## Building

`verify_build_local` runs the SDK's real pipeline and produces an actual `.vrcw`. It catches everything
validation does not. Upload is deliberately not available in this toolchain — the autonomy boundary is local
Build & Test.

If a build fails with "Scene export failed. Check console output for details." and nothing in the project
explains it, the cause is usually environmental rather than content: the SDK stages through
`%TEMP%\<company>\<product>` and dies if temp cleanup removed that folder. `verify_build_local` recreates it
before every build.

`world_validate` runs the SDK's own validations and returns errors, warnings and performance stats as data —
including the warnings that have no public getter and are normally only visible in the SDK panel's GUI.

## Recovering

```
project_checkpoints                      list the checkpoints
project_rollback --target <sha> --confirm true
```

Rollback commits the current state first and anchors it under `refs/agentic/safety/...`, so it is reversible
and cannot be garbage-collected. Afterwards the editor still holds the old scene in memory — reopen it or call
`unity_refresh_assets` before trusting what the editor shows.

## The other half of the tools

Two MCP servers attach to the same editor. The `vrcw_*`, `world_*`, `udon_*`, `scene_*`, `verify_*`,
`budget_*` and `assets_*` tools are the VRChat-specific half. Generic Unity work — baking GI and lightmaps,
NavMesh, prefab operations, the Test Runner, profiling, animation — belongs to the bundled generic server
(`manage_scene`, `manage_gameobject`, `manage_asset`, `manage_editor`, `read_console`, and around forty
others). Prefer the VRChat-specific tool when both could do the job: `scene_apply_spec` is idempotent and
costs one call where per-object editing costs dozens, and `udon_set_fields` writes UdonSharp fields through
the proxy so the values survive a build, which generic component editing does not.

If the generic tools are missing or report no editor, call `companion_status` — it names the cause, which is
always one of two things, and `companion_setup` fixes both.

## Pickups, stations and video screens

The three components that carry their own traps.

**A pickup** needs `VRCPickup` plus a `Rigidbody` and a collider; without the Rigidbody Unity refuses to add
the component at all. Gravity on means the prop settles wherever physics puts it, which is usually what a
world wants and never what a test wants. The Udon side is `OnPickup` / `OnDrop`, parameterless.

**A station** needs `VRCStation` with `stationEnterPlayerLocation` and `stationExitPlayerLocation` transforms
— without them ClientSim warns and the player has nowhere to go. `PlayerMobility` is `Mobile`, `Immobilize`
or `ImmobilizeForVehicle`. The Udon side is `OnStationEntered(VRCPlayerApi)` / `OnStationExited(VRCPlayerApi)`,
and both fire for **remote** players too — guard local-only UI with `player.isLocal`.

**A video screen** is the one with a genuine trap: `UnityEngine.Video.VideoPlayer` is **not** on VRChat's
component whitelist, so adding one gets it silently stripped from the built world. Use
`VRC.SDK3.Video.Components.VRCUnityVideoPlayer` alone — it creates its own player at runtime, which is why
its `_unityPlayer` field is not even serialized. What you do wire is private `[SerializeField]` state that
`scene_apply_spec` can set: `renderMode` (`RenderTexture` or `MaterialOverride`), `targetMaterialRenderer`,
`targetMaterialProperty` (`_MainTex`), `targetAudioSources`, `aspectRatio`, `maximumResolution`, `videoURL`.
`scene_apply_spec` warns about anything not whitelisted — believe the warning; it is the SDK's own list.

In Udon, the field type is `BaseVRCVideoPlayer`, not `VRCUnityVideoPlayer`: only the base is exposed to
Udon. Its API is `PlayURL(VRCUrl)`, `LoadURL`, `Play`, `Stop`, `Pause`, `GetTime`, `SetTime`, `GetDuration`,
`IsPlaying`, `IsReady`, `Loop`. Events are `OnVideoStart`, `OnVideoEnd`, `OnVideoReady`, `OnVideoLoop`,
`OnVideoPause`, `OnVideoPlay` and `OnVideoError(VideoError)`.

**URLs cannot be built at runtime.** `VRCUrl` has no Udon-exposed constructor, so every URL a world can play
has to exist as a field before the build. Set them from the editor: `udon_set_fields` turns a plain string
into a `VRCUrl`, and so does `scene_apply_spec` for the component's own `videoURL`.

## Proving interactions actually work

`verify_clientsim` drives a real player, so these are tested rather than assumed:

```
interact            the use action, through the behaviour's Interact entry point
grab / drop         picks the object up with ClientSim's own hand, so OnPickup and OnDrop really fire
sit / stand         puts the local player into a VRCStation, firing OnStationEntered / OnStationExited
assert_held         is this pickup held, as ClientSim reports it
assert_in_station   is this station occupied
send_event          any Udon event by its entry-point name, including built-ins: _onVideoStart, _onVideoEnd
```

Two limits worth knowing. `grab` and `interact` do not check reachability — the hand takes the object
wherever it is, so "can a player actually get to it" stays `scene_spatial_audit`'s question. And video
playback needs the network, so scenarios drive `_onVideoStart` / `_onVideoEnd` and check the world's reaction
rather than streaming anything.
