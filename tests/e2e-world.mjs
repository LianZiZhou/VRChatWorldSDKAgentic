#!/usr/bin/env node
/**
 * End-to-end: build and verify a small interactive VRChat world using only the tool surface.
 *
 * This is the acceptance criterion for the whole toolchain, and the world it builds is the one the plan
 * specified: a button that opens a door, a counter synchronised across players, a video player, and a prop
 * that can be picked up — plus a chair, because entering a station is the other interaction a world
 * routinely gets wrong. Nothing here touches Unity by hand — every step is a call an agent could make — and
 * it runs the full pipeline in the order the SDK actually requires:
 *
 *   checkpoint -> harden editor -> fix project -> scaffold scene -> generate architecture
 *   -> place objects and SDK components declaratively
 *   -> lint (no Unity) -> write scripts -> compile -> attach -> wire fields
 *   -> audit geometry -> photograph -> budget both platforms
 *   -> verify behaviour in ClientSim (interact, grab, drop, sit, stand, video callbacks)
 *   -> build a real .vrcw -> checkpoint
 *
 * What the ClientSim pass deliberately does not do is stream the video. Playback needs the network and a URL
 * Udon cannot construct at runtime, so the scenario drives the player's callbacks and checks the world reacts
 * correctly; the player component itself is verified by being wired, whitelisted, and surviving the build.
 */
import { existsSync, statSync } from "node:fs";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";
import * as projectGit from "../packages/vrcw-mcp/dist/project.js";
import { spawnSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { writeFileSync, mkdirSync } from "node:fs";

const here = dirname(fileURLToPath(import.meta.url));
const PROJECT = process.env.VRCW_PROJECT ?? "E:/Unity/WorldTest";
const SCENE = "Assets/Scenes/AgenticE2E.unity";
const CLASS = "AgenticE2ECounter";
const PROP_CLASS = "AgenticE2EProp";
const CHAIR_CLASS = "AgenticE2EChair";
const VIDEO_CLASS = "AgenticE2EVideo";

const ROOM = "World/Room";
const BUTTON = "World/Button";
const DISPLAY = "World/Display";
const DOOR = "World/Door";
const PROP = "World/Prop";
const PROP_GLOW = "World/Prop/Glow";
const CHAIR = "World/Chair";
const CHAIR_ENTER = "World/Chair/Enter";
const CHAIR_EXIT = "World/Chair/Exit";
const CHAIR_MENU = "World/Chair/Menu";
const SCREEN = "World/Screen";
const SCREEN_ON = "World/Screen/Playing";

// The door slides aside rather than switching off, so "open" is a position a scenario can measure.
const DOOR_CLOSED = [0, 1.1, 5.1];
const DOOR_OPEN = [2.1, 1.1, 5.1];

// A colour per element, so a render shows which thing is which.
const PALETTE = {
  button: "Assets/Agentic/Materials/E2EButton.mat",
  door: "Assets/Agentic/Materials/E2EDoor.mat",
  prop: "Assets/Agentic/Materials/E2EProp.mat",
  chair: "Assets/Agentic/Materials/E2EChair.mat",
  screen: "Assets/Agentic/Materials/E2EScreen.mat",
  accent: "Assets/Agentic/Materials/E2EAccent.mat",
};

const renderer = (material) => ({
  type: "UnityEngine.MeshRenderer",
  fields: { sharedMaterial: { $asset: material } },
});

const analyzer = join(here, "..", "packages", "udon-analyzer", "bin", "Release", "net8.0",
  process.platform === "win32" ? "udon-analyzer.exe" : "udon-analyzer");

let passed = 0;
let failed = 0;
const timeline = [];

async function step(name, fn) {
  const started = Date.now();
  try {
    const detail = await fn();
    passed++;
    const ms = Date.now() - started;
    timeline.push({ name, ms });
    console.log(`PASS  ${name}  (${(ms / 1000).toFixed(1)}s)`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 6).join("\n      ")}`);
    throw err; // the pipeline is sequential; a failed step invalidates what follows
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

async function runJob(kind, params, waitMs = 900_000) {
  const job = await rpc("job.submit", { kind, params });
  const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
  const deadline = Date.now() + waitMs;
  let last = job;
  while (Date.now() < deadline) {
    try {
      last = await rpc("job.status", { id: job.id }, { timeoutMs: 20_000 });
      if (terminal.has(last.state)) return last;
    } catch {
      /* builds and play mode occupy the main thread */
    }
    await new Promise((r) => setTimeout(r, 1200));
  }
  throw new Error(`job ${kind} never finished: ${JSON.stringify(last).slice(0, 200)}`);
}

const SOURCE = `using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ${CLASS} : UdonSharpBehaviour
{
    public GameObject display;
    public Transform door;
    public Vector3 doorClosed;
    public Vector3 doorOpen;
    public int step;

    [UdonSynced] public int count;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        count = count + step;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        // Telemetry for real-client verification: Udon cannot send data out, so a tagged log line is the
        // only channel verify_multiclient can read back.
        Debug.Log("[AGENTTEL]{\\"counter\\":" + count + "}");
        if (display != null) display.SetActive(count > 0);
        // Odd presses leave the door open, so the door's position is the observable effect of the count.
        if (door != null) door.localPosition = (count % 2 == 1) ? doorOpen : doorClosed;
    }
}
`;

/** A prop that can be picked up, which reports who is holding it. */
const PROP_SOURCE = `using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ${PROP_CLASS} : UdonSharpBehaviour
{
    public GameObject glow;

    [UdonSynced] public bool held;
    [UdonSynced] public int pickupCount;

    public override void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        held = true;
        pickupCount = pickupCount + 1;
        RequestSerialization();
        _Apply();
    }

    public override void OnDrop()
    {
        held = false;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        Debug.Log("[AGENTTEL]{\\"prop\\":{\\"held\\":" + (held ? "true" : "false") + ",\\"pickups\\":" + pickupCount + "}}");
        if (glow != null) glow.SetActive(held);
    }
}
`;

/** A chair that shows a menu to whoever sits in it. */
const CHAIR_SOURCE = `using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ${CHAIR_CLASS} : UdonSharpBehaviour
{
    public GameObject menu;
    public int sitCount;

    public override void OnStationEntered(VRCPlayerApi player)
    {
        // Station events fire for remote players too, and the menu is local UI, so only the local player's
        // entry should touch it.
        if (player == null || !player.isLocal) return;
        sitCount = sitCount + 1;
        if (menu != null) menu.SetActive(true);
        Debug.Log("[AGENTTEL]{\\"chair\\":{\\"seated\\":true,\\"sits\\":" + sitCount + "}}");
    }

    public override void OnStationExited(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        if (menu != null) menu.SetActive(false);
        Debug.Log("[AGENTTEL]{\\"chair\\":{\\"seated\\":false,\\"sits\\":" + sitCount + "}}");
    }
}
`;

/** A video screen: interact to start or stop, and react to the player's own callbacks. */
const VIDEO_SOURCE = `using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ${VIDEO_CLASS} : UdonSharpBehaviour
{
    public BaseVRCVideoPlayer videoPlayer;
    public GameObject playingIndicator;

    // Udon cannot build a VRCUrl at runtime, which is why every video world carries its URLs as fields.
    public VRCUrl url;

    public bool isPlaying;

    public override void Interact()
    {
        if (videoPlayer == null) return;
        if (isPlaying) videoPlayer.Stop();
        else videoPlayer.PlayURL(url);
    }

    public override void OnVideoStart()
    {
        isPlaying = true;
        _Apply();
    }

    public override void OnVideoEnd()
    {
        isPlaying = false;
        _Apply();
    }

    public override void OnVideoError(VideoError videoError)
    {
        isPlaying = false;
        Debug.Log("[AGENTTEL]{\\"video\\":{\\"error\\":\\"" + videoError.ToString() + "\\"}}");
        _Apply();
    }

    private void _Apply()
    {
        Debug.Log("[AGENTTEL]{\\"video\\":{\\"playing\\":" + (isPlaying ? "true" : "false") + "}}");
        if (playingIndicator != null) playingIndicator.SetActive(isPlaying);
    }
}
`;

const SCRIPTS = [
  { className: CLASS, source: SOURCE },
  { className: PROP_CLASS, source: PROP_SOURCE },
  { className: CHAIR_CLASS, source: CHAIR_SOURCE },
  { className: VIDEO_CLASS, source: VIDEO_SOURCE },
];

try {
  // ------------------------------------------------------------------ prepare

  await step("checkpoint the project before changing anything", async () => {
    const result = projectGit.checkpoint(PROJECT, "agentic e2e: before building the world", true);
    return result.committed ? `committed at ${result.head}` : `clean at ${result.head}`;
  });

  await step("editor is reachable, idle and non-interactive", async () => {
    await waitForIdle();
    const hardened = await rpc("bridge.harden_editor", { apply: true, disableDomainReloadOnPlay: true }, { timeoutMs: 60_000 });
    const bad = (hardened.settings ?? []).filter((s) => !s.compliant).map((s) => s.key);
    assert(bad.length === 0, `editor still interactive: ${bad.join(", ")}`);
    const health = await rpc("bridge.health");
    return `${health.methodCount} bridge methods, ${health.jobKinds.length} job kinds`;
  });

  await step("project is configured for VRChat", async () => {
    const fixed = await rpc("world.fix_project", {}, { timeoutMs: 120_000 });
    assert(fixed.buildWouldBeBlockedByLayers === false, "layers or collision matrix still wrong");
    return `layers=${fixed.layersSetUp} matrix=${fixed.collisionMatrixSetUp}`;
  });

  // ------------------------------------------------------------------ build the world

  await step("scaffold a world scene that satisfies the SDK", async () => {
    const world = await rpc("world.scaffold",
      { path: SCENE, addFloor: true, overwrite: true, discardUnsaved: true }, { timeoutMs: 180_000 });
    assert(world.problems.length === 0, `problems: ${JSON.stringify(world.problems)}`);
    assert(world.descriptors[0].validSpawnCount >= 1, "no valid spawn");
    return `${SCENE}: 1 descriptor, ${world.descriptors[0].validSpawnCount} spawn, floor=${world.createdFloor}`;
  });

  await step("generate a room with a doorway", async () => {
    const room = await rpc("geo.generate", {
      kind: "room", path: ROOM, size: [10, 3, 10], thickness: 0.2, floor: true, ceiling: true,
      doors: [{ wall: "north", width: 1.8, height: 2.2, offset: 0 }], saveScene: true,
    }, { timeoutMs: 180_000 });
    for (const piece of room.pieces) {
      const s = piece.localScale;
      assert(s.x > 0 && s.y > 0 && s.z > 0, `degenerate piece ${piece.name}`);
    }
    return `${room.pieceCount} pieces, ~${room.approximateTriangles} tris`;
  });

  await step("place every interactive object declaratively", async () => {
    const applied = await rpc("scene.apply_spec", {
      objects: [
        // The button, its readout, and the door it drives.
        { path: BUTTON, primitive: "Cube", position: [0, 1.2, 3], scale: [0.4, 0.4, 0.15] },
        { path: DISPLAY, primitive: "Sphere", position: [0, 2.2, 3], scale: [0.5, 0.5, 0.5], active: false },
        { path: DOOR, primitive: "Cube", position: DOOR_CLOSED, scale: [1.8, 2.2, 0.2] },

        // A prop to carry. VRCPickup needs a Rigidbody; gravity is off so the prop stays where it is put
        // instead of settling mid-scenario and moving the assertions under our feet.
        {
          path: PROP, primitive: "Cube", position: [2, 1, 0], scale: [0.3, 0.3, 0.3],
          components: [
            { type: "UnityEngine.Rigidbody", fields: { useGravity: false } },
            {
              type: "VRC.SDK3.Components.VRCPickup",
              fields: { pickupable: true, proximity: 2, InteractionText: "Take" },
            },
          ],
        },
        { path: PROP_GLOW, primitive: "Sphere", position: [2, 1.4, 0], scale: [0.15, 0.15, 0.15], active: false },

        // A chair. A station needs somewhere to put the player on entry and somewhere to leave them on exit.
        { path: CHAIR, primitive: "Cube", position: [-2, 0.5, 0], scale: [0.6, 0.1, 0.6] },
        { path: CHAIR_ENTER, position: [-2, 0.6, 0] },
        { path: CHAIR_EXIT, position: [-2.8, 0.1, 0] },
        // Quads face +Z. A menu for someone seated facing +Z has to be turned around to face them back.
        { path: CHAIR_MENU, primitive: "Quad", position: [-2, 1.4, 1.0], rotation: [0, 180, 0], scale: [0.8, 0.5, 1], active: false },

        // A video screen: the Unity player, its audio, and VRChat's wrapper around them.
        // Turned to face into the room: on the -Z wall an unrotated Quad shows the room its back face, and
        // backface culling then makes the screen invisible from everywhere a player can stand.
        { path: SCREEN, primitive: "Quad", position: [0, 2, -4.8], rotation: [0, 180, 0], scale: [4, 2.25, 1] },
        { path: SCREEN_ON, primitive: "Sphere", position: [2.2, 2, -4.8], scale: [0.2, 0.2, 0.2], active: false },
      ],
      saveScene: true,
    }, { timeoutMs: 180_000 });
    assert(applied.warnings.length === 0, `warnings: ${JSON.stringify(applied.warnings)}`);

    // A second pass for the components that reference objects created in the first.
    const wired = await rpc("scene.apply_spec", {
      objects: [
        {
          path: CHAIR,
          components: [
            {
              type: "VRC.SDK3.Components.VRCStation",
              fields: {
                stationEnterPlayerLocation: { $gameObject: CHAIR_ENTER },
                stationExitPlayerLocation: { $gameObject: CHAIR_EXIT },
                PlayerMobility: "Immobilize",
                seated: true,
                canUseStationFromStation: true,
                disableStationExit: false,
              },
            },
          ],
        },
        {
          path: SCREEN,
          components: [
            // No UnityEngine.Video.VideoPlayer here, deliberately: it is not on VRChat's component
            // whitelist and would be stripped out of the built world. VRCUnityVideoPlayer creates its own
            // player at runtime, which is why its _unityPlayer field is not even serialized.
            { type: "UnityEngine.AudioSource", fields: { playOnAwake: false, spatialBlend: 1 } },
            {
              type: "VRC.SDK3.Video.Components.VRCUnityVideoPlayer",
              // All of this is private [SerializeField] wiring — the inspector's job, and with it a screen
              // that actually renders and plays sound rather than a component with nothing attached.
              fields: {
                renderMode: "MaterialOverride",
                targetMaterialRenderer: { $component: { path: SCREEN, type: "UnityEngine.MeshRenderer" } },
                targetMaterialProperty: "_MainTex",
                targetAudioSources: [{ $component: { path: SCREEN, type: "UnityEngine.AudioSource" } }],
                aspectRatio: "FitInside",
                maximumResolution: 720,
                videoURL: "https://example.com/agentic-e2e.mp4",
                loop: false,
                autoPlay: false,
                EnableAutomaticResync: true,
              },
            },
          ],
        },
      ],
      saveScene: true,
    }, { timeoutMs: 180_000 });
    assert(wired.warnings.length === 0, `component warnings: ${JSON.stringify(wired.warnings)}`);

    const components = [...(applied.componentsAdded ?? []), ...(wired.componentsAdded ?? [])]
      .map((c) => String(c.type).split(".").pop());
    return `${applied.created.length + wired.created.length} created, ` +
      `${applied.updated.length + wired.updated.length} updated; components: ${components.join(", ")}`;
  });

  await step("give the world materials, so its renders are readable", async () => {
    // Not decoration. Every primitive arrives with the same white Default-Material, and a survey render of a
    // white cube against a white wall shows nothing at all — the first pass of this world photographed as an
    // empty room. Distinct colours are what make the render a check rather than a formality.
    const palette = [
      { path: PALETTE.button, colour: [0.85, 0.2, 0.15, 1] },
      { path: PALETTE.door, colour: [0.45, 0.3, 0.18, 1] },
      { path: PALETTE.prop, colour: [0.95, 0.75, 0.1, 1] },
      { path: PALETTE.chair, colour: [0.2, 0.45, 0.8, 1] },
      { path: PALETTE.screen, colour: [0.07, 0.07, 0.09, 1] },
      { path: PALETTE.accent, colour: [0.15, 0.8, 0.45, 1] },
    ];
    for (const entry of palette) {
      await rpc("assets.make_material",
        { path: entry.path, shader: "Standard", colors: { _Color: entry.colour }, floats: { _Glossiness: 0.2 } },
        { timeoutMs: 120_000 });
    }

    const painted = await rpc("scene.apply_spec", {
      objects: [
        { path: BUTTON, components: [renderer(PALETTE.button)] },
        { path: DOOR, components: [renderer(PALETTE.door)] },
        { path: PROP, components: [renderer(PALETTE.prop)] },
        { path: PROP_GLOW, components: [renderer(PALETTE.accent)] },
        { path: CHAIR, components: [renderer(PALETTE.chair)] },
        { path: CHAIR_MENU, components: [renderer(PALETTE.accent)] },
        { path: SCREEN, components: [renderer(PALETTE.screen)] },
        { path: SCREEN_ON, components: [renderer(PALETTE.accent)] },
        { path: DISPLAY, components: [renderer(PALETTE.accent)] },
      ],
      saveScene: true,
    }, { timeoutMs: 180_000 });
    assert(painted.warnings.length === 0, `warnings: ${JSON.stringify(painted.warnings)}`);
    return `${palette.length} materials on ${painted.updated.length} objects`;
  });

  // ------------------------------------------------------------------ the script

  await step("lint every script before Unity ever sees it", async () => {
    mkdirSync(join(here, "fixtures"), { recursive: true });
    const staged = SCRIPTS.map(({ className, source }) => {
      const file = join(here, "fixtures", `${className}.cs`);
      writeFileSync(file, source);
      return file;
    });

    const result = spawnSync(analyzer, ["--json", ...staged], { encoding: "utf8", timeout: 120_000 });
    const report = JSON.parse(result.stdout);
    assert(report.errorCount === 0, `linter rejected a script: ${JSON.stringify(report.diagnostics)}`);
    // Milliseconds, versus the tens of seconds a Unity round trip costs - per script.
    return `${staged.length} scripts clean in the linter (${report.warningCount} warning(s)), no Unity round trip spent`;
  });

  await step("write the scripts and let Unity compile them", async () => {
    for (const { className, source } of SCRIPTS) {
      await rpc("udon.create_script", { className, folder: "Assets/Udon", code: source }, { timeoutMs: 120_000 });
    }
    // One refresh for all four: separately they would each cost a domain reload, and they compile together.
    await rpc("unity.refresh_assets", { saveAssets: true, forceSynchronous: true }, { timeoutMs: 180_000 }).catch(() => {});
    const compiled = await waitForIdle();
    assert(compiled.errorCount === 0, `C# errors: ${JSON.stringify(compiled.diagnostics?.slice(0, 3))}`);

    // Second pass creates the program assets, which can only be linked once the types exist.
    for (const { className, source } of SCRIPTS) {
      const second = await rpc("udon.create_script", { className, folder: "Assets/Udon", code: source },
        { timeoutMs: 120_000 });
      assert(second.programAssetExists, `program asset was not created for ${className}`);
    }
    return `${SCRIPTS.length} scripts written, program assets ready`;
  });

  await step("compile the Udon programs", async () => {
    let job = await runJob("udon.compile", {});
    if (job.state === "interrupted" || job.result?.stage === "upgrade") {
      await waitForIdle();
      job = await runJob("udon.compile", {});
    }
    assert(job.result?.ok === true, `compile failed: ${JSON.stringify(job.result).slice(0, 300)}`);
    return `stage=${job.result.stage} errors=${job.result.errorCount}`;
  });

  await step("attach the behaviours and wire their fields", async () => {
    const wiring = [
      {
        gameObject: BUTTON, type: CLASS,
        fields: {
          step: 1, count: 0,
          display: { $gameObject: DISPLAY },
          door: { $gameObject: DOOR },
          doorClosed: DOOR_CLOSED,
          doorOpen: DOOR_OPEN,
        },
      },
      { gameObject: PROP, type: PROP_CLASS, fields: { glow: { $gameObject: PROP_GLOW }, held: false, pickupCount: 0 } },
      { gameObject: CHAIR, type: CHAIR_CLASS, fields: { menu: { $gameObject: CHAIR_MENU }, sitCount: 0 } },
      {
        gameObject: SCREEN, type: VIDEO_CLASS,
        fields: {
          videoPlayer: { $component: { path: SCREEN, type: "VRC.SDK3.Video.Components.VRCUnityVideoPlayer" } },
          playingIndicator: { $gameObject: SCREEN_ON },
          // A string becomes a VRCUrl: Udon cannot construct one at runtime, so the editor has to.
          url: "https://example.com/agentic-e2e.mp4",
          isPlaying: false,
        },
      },
    ];

    const summary = [];
    for (const entry of wiring) {
      const attached = await rpc("udon.attach", { gameObject: entry.gameObject, type: entry.type },
        { timeoutMs: 60_000 });
      assert(attached.programAssetLinked, `program asset not linked for ${entry.type}`);

      const set = await rpc("udon.set_fields",
        { gameObject: entry.gameObject, type: entry.type, fields: entry.fields, saveScene: true },
        { timeoutMs: 60_000 });
      assert(set.copiedToUdon, `CopyProxyToUdon failed for ${entry.type}: ${set.copyError}`);

      // The assertion that matters: the values reached the UdonBehaviour, not just the proxy.
      const read = await rpc("udon.read_vars", { gameObject: entry.gameObject, type: entry.type });
      assert(read.mismatchCount === 0, `${entry.type}: proxy and Udon disagree: ${JSON.stringify(read.variables)}`);
      summary.push(`${entry.type}(${read.variables.filter((v) => v.presentOnUdonBehaviour).length} fields)`);
    }
    return `on the UdonBehaviour: ${summary.join(", ")}`;
  });

  await step("the video URL survived as a real VRCUrl", async () => {
    // A URL that failed to convert reads back empty and only fails in the world, where nobody is watching.
    const read = await rpc("udon.read_vars", { gameObject: SCREEN, type: VIDEO_CLASS });
    const url = read.variables.find((v) => v.name === "url");
    const player = read.variables.find((v) => v.name === "videoPlayer");
    // VRCUrl keeps its content private, so the bridge reports the type's own ToString.
    assert(url && String(url.udonValue?.toString ?? "").includes("agentic-e2e.mp4"),
      `url did not reach Udon as a VRCUrl: ${JSON.stringify(url)}`);
    assert(player && player.presentOnUdonBehaviour, `video player reference missing: ${JSON.stringify(player)}`);
    return "url and the BaseVRCVideoPlayer reference are both on the UdonBehaviour";
  });

  // ------------------------------------------------------------------ inspect

  await step("geometry audit is clean", async () => {
    const audit = await rpc("scene.spatial_audit", { gridStep: 1.5 }, { timeoutMs: 180_000 });
    assert(audit.spawns[0]?.grounded === true, `spawn not grounded: ${JSON.stringify(audit.spawns[0])}`);
    assert(audit.errorCount === 0, `errors: ${JSON.stringify(audit.findings.filter((f) => f.severity === "error"))}`);
    return `spawn grounded at ${audit.spawns[0].groundDistance.toFixed(2)}m, coverage ${(audit.floorCoverage.coverage * 100).toFixed(0)}%, ${audit.warningCount} warning(s)`;
  });

  await step("photograph the world from the spawn", async () => {
    // Four yaw steps rather than two: with two, the prop and the chair sit exactly at the edges and appear
    // in no frame at all, which makes the renders look like an empty room and the check like a formality.
    const shots = await rpc("scene.survey_shots",
      { mode: "all", width: 800, height: 450, yawSteps: 4, orbitCount: 2, label: "e2e" }, { timeoutMs: 180_000 });
    let smallest = Infinity;
    for (const shot of shots.shots) {
      assert(existsSync(shot.file), `missing ${shot.file}`);
      smallest = Math.min(smallest, statSync(shot.file).size);
    }
    assert(smallest > 2000, `smallest render is ${smallest} bytes, so the views are probably blank`);
    return `${shots.shotCount} renders, smallest ${Math.round(smallest / 1024)}KB, in ${shots.directory}`;
  });

  await step("budget is within limits on both platforms", async () => {
    const pc = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
    const android = await rpc("budget.report", { platform: "Android" }, { timeoutMs: 180_000 });
    assert(pc.errorCount === 0, `PC budget errors: ${JSON.stringify(pc.findings.filter((f) => f.severity === "error"))}`);
    assert(android.errorCount === 0, `Android budget errors: ${JSON.stringify(android.findings.filter((f) => f.severity === "error"))}`);
    return `tris=${pc.scene.triangles} materials=${pc.scene.uniqueMaterials} | PC ${pc.warningCount} warn, Android ${android.warningCount} warn`;
  });

  // ------------------------------------------------------------------ verify behaviour

  await step("ClientSim proves every interaction works", async () => {
    const job = await runJob("verify.clientsim", {
      name: "button opens the door, the prop is carried, the chair seats the player, the video reacts",
      steps: [
        { wait_frames: 20 },

        // The button: a synchronised counter whose observable effect is the door.
        { assert_var: { gameObject: BUTTON, type: CLASS, name: "count", equals: 0 } },
        { assert_position: { gameObject: DOOR, near: DOOR_CLOSED, tolerance: 0.05 } },
        { assert_active: { gameObject: DISPLAY, equals: false } },
        { interact: { gameObject: BUTTON, type: CLASS } },
        { wait_frames: 3 },
        { assert_var: { gameObject: BUTTON, type: CLASS, name: "count", equals: 1 } },
        { assert_position: { gameObject: DOOR, near: DOOR_OPEN, tolerance: 0.05 } },
        { assert_active: { gameObject: DISPLAY, equals: true } },
        { interact: { gameObject: BUTTON, type: CLASS } },
        { wait_frames: 3 },
        { assert_var: { gameObject: BUTTON, type: CLASS, name: "count", equals: 2 } },
        { assert_position: { gameObject: DOOR, near: DOOR_CLOSED, tolerance: 0.05 } },

        // The prop: taken by ClientSim's own hand, so OnPickup really fires.
        { assert_held: { gameObject: PROP, equals: false } },
        { grab: { gameObject: PROP, hand: "right" } },
        { wait_frames: 3 },
        { assert_held: { gameObject: PROP, equals: true } },
        { assert_var: { gameObject: PROP, type: PROP_CLASS, name: "held", equals: true } },
        { assert_var: { gameObject: PROP, type: PROP_CLASS, name: "pickupCount", equals: 1 } },
        { assert_active: { gameObject: PROP_GLOW, equals: true } },
        { drop: { gameObject: PROP } },
        { wait_frames: 3 },
        { assert_held: { gameObject: PROP, equals: false } },
        { assert_var: { gameObject: PROP, type: PROP_CLASS, name: "held", equals: false } },
        { assert_active: { gameObject: PROP_GLOW, equals: false } },

        // The chair: entering a station is the other interaction that needs a real player.
        { assert_in_station: { gameObject: CHAIR, equals: false } },
        { sit: { gameObject: CHAIR } },
        { wait_frames: 3 },
        { assert_in_station: { gameObject: CHAIR, equals: true } },
        { assert_var: { gameObject: CHAIR, type: CHAIR_CLASS, name: "sitCount", equals: 1 } },
        { assert_active: { gameObject: CHAIR_MENU, equals: true } },
        { stand: { gameObject: CHAIR } },
        { wait_frames: 3 },
        { assert_in_station: { gameObject: CHAIR, equals: false } },
        { assert_active: { gameObject: CHAIR_MENU, equals: false } },

        // The video player's callbacks, without streaming anything: the events are what a world reacts to,
        // and playback would need the network and a URL Udon cannot build at runtime.
        { assert_var: { gameObject: SCREEN, type: VIDEO_CLASS, name: "isPlaying", equals: false } },
        { send_event: { gameObject: SCREEN, type: VIDEO_CLASS, event: "_onVideoStart" } },
        { wait_frames: 3 },
        { assert_var: { gameObject: SCREEN, type: VIDEO_CLASS, name: "isPlaying", equals: true } },
        { assert_active: { gameObject: SCREEN_ON, equals: true } },
        { send_event: { gameObject: SCREEN, type: VIDEO_CLASS, event: "_onVideoEnd" } },
        { wait_frames: 3 },
        { assert_var: { gameObject: SCREEN, type: VIDEO_CLASS, name: "isPlaying", equals: false } },
        { assert_active: { gameObject: SCREEN_ON, equals: false } },

        { assert_no_errors: true },
      ],
    });
    assert(job.result?.ok === true,
      `scenario failed: ${JSON.stringify((job.result?.steps ?? []).filter((s) => !s.ok)).slice(0, 400)}`);
    const ignored = (job.result.steps ?? []).find((s) => s.step === "assert_no_errors")?.ignoredCount ?? 0;
    return `${job.result.stepCount} steps, 0 failures (${ignored} known SDK noise entr(y/ies) reported separately)`;
  });

  await step("the telemetry contract produced parseable output for every feature", async () => {
    // The world logs [AGENTTEL] lines; in ClientSim they land in the Unity console rather than a client log,
    // which is the same contract verify_multiclient reads from a real client.
    const console_ = await rpc("unity.console", { limit: 400, grep: "[AGENTTEL]" });
    assert(console_.returned > 0, "no telemetry lines were captured");

    // Every feature reports under its own key, so a silent one is visible here rather than in a world.
    const seen = {};
    for (const entry of console_.entries) {
      const text = String(entry.msg);
      const payload = JSON.parse(text.slice(text.indexOf("{")));
      for (const key of Object.keys(payload)) seen[key] = payload[key];
    }
    for (const feature of ["counter", "prop", "chair", "video"]) {
      assert(feature in seen, `no telemetry from ${feature}; got ${Object.keys(seen).join(", ") || "nothing"}`);
    }
    return `${console_.returned} line(s): counter=${seen.counter}, prop pickups=${seen.prop.pickups}, ` +
      `chair sits=${seen.chair.sits}, video playing=${seen.video.playing}`;
  });

  // ------------------------------------------------------------------ build

  await step("build a real .vrcw world bundle", async () => {
    const job = await runJob("verify.build_local", {}, 1_800_000);
    assert(job.result?.ok === true,
      `build failed at ${job.result?.stage}: ${job.result?.message} ${JSON.stringify(job.result?.validationErrors ?? "")}`);
    assert(job.result.bundleBytes > 0, "bundle is empty");
    return `${job.result.platform} ${job.result.bundleMegabytes.toFixed(2)}MB in ${job.result.elapsedSeconds.toFixed(1)}s`;
  });

  await step("the built bundle is measured against VRChat's limit", async () => {
    const report = await rpc("budget.report", { platform: "Windows" }, { timeoutMs: 180_000 });
    assert(report.bundle.found === true, "the build was not found by the budget report");
    assert(report.bundle.withinLimit === true, `over limit: ${JSON.stringify(report.bundle)}`);
    return `${report.bundle.megabytes.toFixed(2)}MB; ${report.bundle.limitNote ?? `${report.bundle.percentOfLimit.toFixed(1)}% of the limit`}`;
  });

  await step("build the same world for Quest", async () => {
    // Both platforms, because a world that only builds for PC is half a deliverable — and because switching
    // target is the step most likely to surprise: it reloads the script domain, so anything in flight dies,
    // and Unity reimports every asset for the new platform.
    await rpc("world.switch_platform", { platform: "Android" }, { timeoutMs: 900_000 });
    await waitForIdle(900_000);
    const onAndroid = await rpc("world.build_status", {}, { timeoutMs: 120_000 });
    assert(onAndroid.activeBuildTarget === "Android",
      `still on ${onAndroid.activeBuildTarget} after switching`);

    const job = await runJob("verify.build_local", {}, 1_800_000);
    assert(job.result?.ok === true,
      `Android build failed at ${job.result?.stage}: ${job.result?.message} ${JSON.stringify(job.result?.validationErrors ?? "")}`);
    assert(job.result.bundleBytes > 0, "Android bundle is empty");

    const report = await rpc("budget.report", { platform: "Android" }, { timeoutMs: 180_000 });
    assert(report.bundle.withinLimit === true, `over the mobile limit: ${JSON.stringify(report.bundle)}`);
    const mobile = `${job.result.bundleMegabytes.toFixed(2)}MB, ${report.bundle.percentOfLimit.toFixed(1)}% of the mobile limit`;

    // Leave the project as it was found: an editor left on Android is a surprise for the next run.
    await rpc("world.switch_platform", { platform: "Windows" }, { timeoutMs: 900_000 });
    await waitForIdle(900_000);
    const back = await rpc("world.build_status", {}, { timeoutMs: 120_000 });
    assert(back.activeBuildTarget === "StandaloneWindows64", `did not switch back: ${back.activeBuildTarget}`);
    return `${mobile}; switched back to ${back.activeBuildTarget}`;
  });

  await step("checkpoint the finished world", async () => {
    const result = projectGit.checkpoint(PROJECT, "agentic e2e: world built and verified", false);
    return result.committed ? `committed ${result.fileCount} files at ${result.head}` : `nothing changed (${result.reason})`;
  });
} catch {
  // The first failure already printed; the summary below reports the outcome.
}

const total = timeline.reduce((sum, t) => sum + t.ms, 0);
console.log(`\n${passed}/${passed + failed} steps passed in ${(total / 1000).toFixed(0)}s`);
if (failed === 0) {
  const slowest = [...timeline].sort((a, b) => b.ms - a.ms).slice(0, 3);
  console.log(`slowest: ${slowest.map((s) => `${s.name} ${(s.ms / 1000).toFixed(1)}s`).join(" | ")}`);
  console.log("\nA VRChat world with a button-operated door, a synchronised counter, a video player, a");
  console.log("carryable prop and a station was scaffolded, scripted, verified and built with no human in");
  console.log("the editor.");
}
process.exit(failed > 0 ? 1 : 0);
