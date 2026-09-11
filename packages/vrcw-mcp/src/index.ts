#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { listInstances } from "./registry.js";
import { ping, rpc, BridgeError } from "./bridge.js";
import * as projectGit from "./project.js";

const MAX_TEXT = 120_000;

const server = new McpServer({ name: "vrcw-mcp", version: "0.1.0" });

/** Every tool takes an optional project hint, since several editors can be open at once. */
const projectArg = {
  project: z
    .string()
    .optional()
    .describe("Project path or name, when more than one Unity Editor is running."),
};

type ToolResult = { content: Array<{ type: "text"; text: string }>; isError?: boolean };

function render(data: unknown): ToolResult {
  let text = typeof data === "string" ? data : JSON.stringify(data, null, 2);
  if (text.length > MAX_TEXT) {
    text = text.slice(0, MAX_TEXT) + `\n...[truncated, ${text.length - MAX_TEXT} more chars]`;
  }
  return { content: [{ type: "text", text }] };
}

function renderError(err: unknown): ToolResult {
  if (err instanceof BridgeError) {
    const hint =
      err.code === "EDITOR_BUSY"
        ? "\n\nHint: the editor is compiling, importing, or blocked on a modal dialog. Poll unity_ping " +
          "(which never needs the main thread) and retry once pumpTicks is advancing again."
        : "";
    return {
      content: [{ type: "text", text: `[${err.code}] ${err.message}${err.stack_ ? "\n\n" + err.stack_ : ""}${hint}` }],
      isError: true,
    };
  }
  return { content: [{ type: "text", text: (err as Error).message ?? String(err) }], isError: true };
}

function tool(
  name: string,
  description: string,
  inputSchema: Record<string, z.ZodTypeAny>,
  handler: (args: any) => Promise<unknown>
): void {
  server.registerTool(name, { description, inputSchema }, async (args: any) => {
    try {
      return render(await handler(args));
    } catch (err) {
      return renderError(err);
    }
  });
}

// ---------------------------------------------------------------- discovery

tool(
  "unity_instances",
  "List Unity Editors that have the agentic bridge loaded, including stale entries from editors that crashed.",
  {},
  async () => listInstances().map(({ token, ...rest }) => ({ ...rest, tokenPresent: Boolean(token) }))
);

tool(
  "unity_ping",
  "Liveness probe answered without touching Unity's main thread. Use this to tell 'editor wedged or " +
    "reloading' apart from 'editor gone': if ping succeeds but other calls return EDITOR_BUSY, the " +
    "process is alive and the main thread is blocked. A stalled secondsSincePump is the signal to watch.",
  { ...projectArg },
  async (a) => ping(a.project)
);

tool(
  "unity_health",
  "Bridge version, pump health, queue depth and registered job kinds.",
  { ...projectArg },
  async (a) => rpc("bridge.health", {}, { project: a.project })
);

tool(
  "unity_methods",
  "List every method the editor-side bridge exposes, with its main-thread requirement and timeout.",
  { ...projectArg },
  async (a) => rpc("bridge.describe", {}, { project: a.project })
);

// ---------------------------------------------------------------- observation

tool(
  "unity_harden_editor",
  "Turn off the editor's interactive prompts. Call this once after the editor starts, before writing any " +
    "assets.\n" +
    "A modal dialog is the worst failure mode here: it halts Unity's update loop, so the bridge's pump " +
    "stops and every call returns EDITOR_BUSY with nobody to click the button. Unity's \"detected external " +
    "changes, reload?\" prompt only exists when Asset Pipeline Auto Refresh is off, so this makes sure it " +
    "is on, along with the other prefs that can raise a dialog during asset work.",
  {
    ...projectArg,
    apply: z.boolean().optional().describe("False reports what would change without changing it. Default true."),
    disableDomainReloadOnPlay: z
      .boolean()
      .optional()
      .describe(
        "Also disable domain reload on entering play mode. Required by ClientSim's test guidance and much " +
          "faster, but it changes play-mode semantics (statics are not reset), so it is opt-in."
      ),
  },
  async (a) =>
    rpc(
      "bridge.harden_editor",
      { apply: a.apply ?? true, disableDomainReloadOnPlay: a.disableDomainReloadOnPlay ?? false },
      { project: a.project }
    )
);

tool(
  "unity_editor_state",
  "Open scenes, active build target, selection, and the two VRChat preconditions for autonomous work: " +
    "whether the SDK control panel window is open and whether the account is logged in. Both are " +
    "required before any build call can succeed.",
  { ...projectArg },
  async (a) => rpc("unity.editor_state", {}, { project: a.project })
);

tool(
  "unity_console",
  "Read console output after a sequence number. Poll with the latestSeq from the previous call to get " +
    "only what is new; seq numbers survive domain reloads, unlike Unity's own console.",
  {
    ...projectArg,
    sinceSeq: z.number().int().min(0).optional().describe("Return entries with seq greater than this. Default 0."),
    limit: z.number().int().min(1).max(2000).optional().describe("Max entries, newest kept. Default 200."),
    level: z.enum(["all", "error", "warning"]).optional().describe("warning includes errors. Default all."),
    grep: z.string().optional().describe("Case-insensitive substring filter on the message."),
  },
  async (a) =>
    rpc(
      "unity.console",
      { sinceSeq: a.sinceSeq ?? 0, limit: a.limit ?? 200, level: a.level ?? "all", grep: a.grep },
      { project: a.project }
    )
);

tool(
  "unity_compile_status",
  "Structured C# compile diagnostics plus live compiling/importing flags. Check this before any " +
    "UdonSharp compile: UdonSharpCompilerV1 silently does nothing while the project has C# errors, so " +
    "a U# compile would report neither success nor failure.",
  { ...projectArg },
  async (a) => rpc("unity.compile_status", {}, { project: a.project })
);

// ---------------------------------------------------------------- execution

tool(
  "unity_eval_csharp",
  "Compile and run a C# snippet inside the editor, returning its value plus any console output it " +
    "produced. This is the general escape hatch for anything the typed tools do not cover.\n" +
    "Body is statements; use 'return x;' to return a value, or pass a single bare expression with no " +
    "semicolon. System/Collections/Linq/IO/Reflection, UnityEngine(.SceneManagement) and " +
    "UnityEditor(.SceneManagement) are already imported. Add VRChat namespaces via usings, e.g. " +
    "VRC.SDKBase, VRC.SDK3.Components, VRC.Udon, UdonSharp, UdonSharpEditor, VRC.SDK3.Editor.\n" +
    "Note this is editor code, not Udon: Linq, generics and try/catch are all fine here even though " +
    "UdonSharp forbids them in world scripts.",
  {
    ...projectArg,
    code: z.string().describe("C# statements, or one bare expression."),
    usings: z.array(z.string()).optional().describe("Extra namespaces to import."),
    maxDepth: z.number().int().min(1).max(12).optional().describe("Serialization depth for the returned value. Default 6."),
    includeReferences: z
      .boolean()
      .optional()
      .describe("Also return the assembly paths the snippet was compiled against. For debugging reference problems."),
    timeoutMs: z.number().int().min(1000).max(600_000).optional(),
  },
  async (a) =>
    rpc(
      "unity.eval_csharp",
      {
        code: a.code,
        usings: a.usings ?? [],
        maxDepth: a.maxDepth ?? 6,
        includeReferences: a.includeReferences ?? false,
      },
      { project: a.project, timeoutMs: a.timeoutMs ?? 120_000 }
    )
);

tool(
  "unity_invoke_static",
  "Call a static method by name through reflection. Prefer this over eval for single SDK calls: it " +
    "needs no compiler and reaches internal members, which matters because the VRChat SDK grants " +
    "InternalsVisibleTo only to its own assemblies.\n" +
    "Examples: UpdateLayers.SetupEditorLayers (global namespace), " +
    "UdonSharp.Compiler.UdonSharpCompilerV1.CompileSync, VRC.Udon.UdonNetworkTypes.CanSync.",
  {
    ...projectArg,
    type: z.string().describe("Full type name, assembly-qualified name, or an unambiguous short name."),
    method: z.string(),
    args: z.array(z.any()).optional().describe("Positional arguments; converted to the parameter types."),
    nonPublic: z.boolean().optional().describe("Include non-public members. Default true."),
    timeoutMs: z.number().int().min(1000).max(600_000).optional(),
  },
  async (a) =>
    rpc(
      "unity.invoke_static",
      { type: a.type, method: a.method, args: a.args ?? [], nonPublic: a.nonPublic ?? true },
      { project: a.project, timeoutMs: a.timeoutMs ?? 60_000 }
    )
);

tool(
  "unity_get_static",
  "Read a static field or property, including non-public ones.",
  {
    ...projectArg,
    type: z.string(),
    member: z.string(),
    nonPublic: z.boolean().optional(),
  },
  async (a) =>
    rpc(
      "unity.get_static",
      { type: a.type, member: a.member, nonPublic: a.nonPublic ?? true },
      { project: a.project }
    )
);

tool(
  "unity_menu_item",
  'Execute an editor menu item. Needed for "VRChat SDK/Show Control Panel", without which ' +
    "TryGetBuilder refuses to hand out the world builder at all.",
  { ...projectArg, path: z.string().describe('Menu path, e.g. "VRChat SDK/Show Control Panel".') },
  async (a) => rpc("unity.menu_item", { path: a.path }, { project: a.project })
);

tool(
  "unity_refresh_assets",
  "AssetDatabase.Refresh, optionally saving dirty assets first. Call after writing .cs files; a domain " +
    "reload normally follows, so expect the next call or two to retry through a reconnect.",
  {
    ...projectArg,
    saveAssets: z.boolean().optional(),
    forceSynchronous: z.boolean().optional(),
  },
  async (a) =>
    rpc(
      "unity.refresh_assets",
      { saveAssets: a.saveAssets ?? false, forceSynchronous: a.forceSynchronous ?? true },
      { project: a.project, timeoutMs: 180_000 }
    )
);

// ---------------------------------------------------------------- jobs

tool(
  "unity_job_submit",
  "Start a long-running editor job and get an id back immediately. Long work (UdonSharp compiles, " +
    "world builds, lightmap bakes, client tests) must go through jobs: a blocking call would both " +
    "exceed client timeouts and stall the update loop the SDK's async build needs to progress.",
  {
    ...projectArg,
    kind: z.string().describe("Job kind; unity_health lists the registered ones."),
    params: z.record(z.any()).optional(),
  },
  async (a) => rpc("job.submit", { kind: a.kind, params: a.params ?? {} }, { project: a.project })
);

tool(
  "unity_job_status",
  "Poll one job. Answered without the main thread, so it keeps working while the job itself occupies it.",
  { ...projectArg, id: z.string() },
  async (a) => rpc("job.status", { id: a.id }, { project: a.project })
);

tool(
  "unity_job_list",
  "Recent jobs, newest first. State 'interrupted' means a domain reload or crash killed the job " +
    "mid-flight and it cannot resume; resubmit it.",
  { ...projectArg, limit: z.number().int().min(1).max(200).optional() },
  async (a) => rpc("job.list", { limit: a.limit ?? 25 }, { project: a.project })
);

tool(
  "unity_job_cancel",
  "Ask a running job to stop at its next yield point.",
  { ...projectArg, id: z.string() },
  async (a) => rpc("job.cancel", { id: a.id }, { project: a.project })
);

tool(
  "unity_job_wait",
  "Poll a job until it reaches a terminal state or the wait budget runs out. Saves a round trip per " +
    "poll; returns the last observed record either way, with timedOut set when still running.",
  {
    ...projectArg,
    id: z.string(),
    waitMs: z.number().int().min(1000).max(3_600_000).optional().describe("Total budget. Default 300000."),
    pollMs: z.number().int().min(250).max(30_000).optional().describe("Interval between polls. Default 1500."),
  },
  async (a) => {
    const deadline = Date.now() + (a.waitMs ?? 300_000);
    const pollMs = a.pollMs ?? 1500;
    const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
    let last: any = null;
    while (Date.now() < deadline) {
      try {
        last = await rpc<any>("job.status", { id: a.id }, { project: a.project, timeoutMs: 15_000 });
        if (terminal.has(String(last?.state))) return last;
      } catch (err) {
        // A reload during a long job is normal; keep polling and let the reconnect logic work.
        if (err instanceof BridgeError && err.code !== "EDITOR_BUSY") throw err;
      }
      await new Promise((r) => setTimeout(r, pollMs));
    }
    return { timedOut: true, lastObserved: last };
  }
);

// ---------------------------------------------------------------- udonsharp

tool(
  "udon_status",
  "UdonSharp program assets, whether any script has an error, and the last compile's structured diagnostics.",
  { ...projectArg },
  async (a) => rpc("udon.status", {}, { project: a.project })
);

tool(
  "udon_create_script",
  "Write an UdonSharpBehaviour .cs file and create its program asset.\n" +
    "Deliberately a two-pass operation, because nothing in the SDK auto-creates a program asset and the " +
    "asset can only be linked once Unity has compiled the script: call it once to write the file, let " +
    "Unity compile (unity_refresh_assets then poll unity_compile_status), then call it again with the same " +
    "arguments — it is idempotent — and it creates the program asset. The response's nextStep says which " +
    "pass you are on.\n" +
    "UdonSharp requires the class name to equal the file name, so className must match the class in code.",
  {
    ...projectArg,
    className: z.string().describe("Class name; also the file name."),
    code: z.string().optional().describe("Full file contents. Omitted: a minimal UdonSharpBehaviour template."),
    folder: z.string().optional().describe("Target folder under Assets/. Default Assets/Udon."),
    createProgramAsset: z.boolean().optional(),
  },
  async (a) =>
    rpc(
      "udon.create_script",
      { className: a.className, code: a.code, folder: a.folder, createProgramAsset: a.createProgramAsset ?? true },
      { project: a.project, timeoutMs: 120_000 }
    )
);

tool(
  "udon_compile",
  "Compile all UdonSharp programs and return structured diagnostics, waiting for completion.\n" +
    "Checks Unity's own C# compile first and reports those errors instead: UdonSharpCompilerV1 silently " +
    "does nothing while the project has C# errors, so skipping that check yields a compile that reports " +
    "neither success nor failure. Also runs the upgrade pass, which fresh program assets need before " +
    "their fields can be written.",
  {
    ...projectArg,
    isEditorBuild: z.boolean().optional().describe("False compiles as for an upload. Default true."),
    waitMs: z.number().int().min(5000).max(1_800_000).optional(),
  },
  async (a) => {
    const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
    const budget = a.waitMs ?? 600_000;

    const runOnce = async () => {
      const job: any = await rpc("job.submit", { kind: "udon.compile", params: { isEditorBuild: a.isEditorBuild ?? true } }, { project: a.project });
      const deadline = Date.now() + budget;
      let last: any = job;
      while (Date.now() < deadline) {
        try {
          last = await rpc("job.status", { id: job.id }, { project: a.project, timeoutMs: 20_000 });
          if (terminal.has(String(last.state))) return last;
        } catch (err) {
          if (err instanceof BridgeError && err.code !== "EDITOR_BUSY") throw err;
        }
        await new Promise((r) => setTimeout(r, 1000));
      }
      return { ...last, timedOut: true };
    };

    let result = await runOnce();
    // The upgrade pass rewrites assets and can trigger a domain reload that interrupts the job. One
        // automatic retry is the documented response and saves the caller a round trip.
    const needsRetry =
      String(result.state) === "interrupted" || (result.result && result.result.stage === "upgrade");
    if (needsRetry) {
      result = await runOnce();
      (result as any).retriedAfterUpgradePass = true;
    }
    return result;
  }
);

tool(
  "udon_attach",
  "Attach an UdonSharpBehaviour to a GameObject, creating and wiring its hidden backing UdonBehaviour. " +
    "Idempotent: reports the existing component rather than adding a second. Requires the script to be " +
    "compiled and to have a program asset.",
  {
    ...projectArg,
    gameObject: z.string().describe('Hierarchy path, e.g. "VRCWorld/Door". Inactive objects are found too.'),
    type: z.string().describe("Behaviour class name."),
    saveScene: z.boolean().optional(),
  },
  async (a) =>
    rpc("udon.attach", { gameObject: a.gameObject, type: a.type, saveScene: a.saveScene ?? false }, { project: a.project })
);

tool(
  "udon_set_fields",
  "Set public fields on an attached UdonSharpBehaviour and copy them onto the backing UdonBehaviour.\n" +
    "Always use this rather than writing the component through a generic Unity tool: values set only on " +
    "the proxy MonoBehaviour never reach the backing UdonBehaviour, so they are silently missing at " +
    "runtime and in the uploaded world. This runs CopyProxyToUdon for you.\n" +
    "Object references are named explicitly: { \"$gameObject\": \"VRCWorld/Door\" }, " +
    '{ "$component": { "path": "...", "type": "Animator" } }, { "$asset": "Assets/M.mat" }. ' +
    "Vectors and colours accept {x,y,z} / {r,g,b,a} or an array.",
  {
    ...projectArg,
    gameObject: z.string(),
    type: z.string(),
    fields: z.record(z.any()).describe("Field name to value."),
    saveScene: z.boolean().optional().describe("Default true."),
  },
  async (a) =>
    rpc(
      "udon.set_fields",
      { gameObject: a.gameObject, type: a.type, fields: a.fields, saveScene: a.saveScene ?? true },
      { project: a.project }
    )
);

tool(
  "udon_read_vars",
  "Read a behaviour's values from both the proxy and the backing UdonBehaviour. Use it to verify writes " +
    "landed: mismatchCount > 0 means the values were never copied to Udon and will not exist at runtime.",
  {
    ...projectArg,
    gameObject: z.string(),
    type: z.string(),
    names: z.array(z.string()).optional().describe("Omit for every public field."),
  },
  async (a) =>
    rpc("udon.read_vars", { gameObject: a.gameObject, type: a.type, names: a.names ?? [] }, { project: a.project })
);

tool(
  "udon_list_behaviours",
  "Every UdonSharpBehaviour in the loaded scenes, with GameObject path and whether its program asset is linked.",
  { ...projectArg },
  async (a) => rpc("udon.list_behaviours", {}, { project: a.project })
);

// ---------------------------------------------------------------- world & scene

tool(
  "world_project_status",
  "Whether VRChat layers, the collision matrix and player settings are configured.",
  { ...projectArg },
  async (a) => rpc("world.project_status", {}, { project: a.project })
);

tool(
  "world_fix_project",
  "Apply VRChat's layer and collision-matrix setup. Idempotent.\n" +
    "Do this before any build: the SDK refuses outright with \"You must address Layers and Collision " +
    "Matrix issues before you can build\". The SDK's own buttons for this sit behind confirmation dialogs, " +
    "which would freeze an unattended editor, so this calls the underlying operations directly.",
  {
    ...projectArg,
    layers: z.boolean().optional(),
    collisionMatrix: z.boolean().optional(),
    playerSettings: z.boolean().optional(),
  },
  async (a) =>
    rpc(
      "world.fix_project",
      { layers: a.layers ?? true, collisionMatrix: a.collisionMatrix ?? true, playerSettings: a.playerSettings ?? true },
      { project: a.project, timeoutMs: 120_000 }
    )
);

tool(
  "world_scaffold",
  "Create a minimal valid VRChat world scene: one VRCSceneDescriptor with a PipelineManager on the same " +
    "GameObject, at least one spawn point, a reference camera and a floor.\n" +
    "Refuses to discard unsaved scenes unless discardUnsaved is set, and refuses to replace an existing " +
    "scene unless overwrite is set.",
  {
    ...projectArg,
    path: z.string().optional().describe("Scene path under Assets/. Default Assets/Scenes/World.unity."),
    addFloor: z.boolean().optional(),
    overwrite: z.boolean().optional(),
    discardUnsaved: z.boolean().optional().describe("Permit losing unsaved changes in the open scenes."),
  },
  async (a) =>
    rpc(
      "world.scaffold",
      { path: a.path, addFloor: a.addFloor ?? true, overwrite: a.overwrite ?? false, discardUnsaved: a.discardUnsaved ?? false },
      { project: a.project, timeoutMs: 180_000 }
    )
);

tool(
  "world_describe_scene",
  "The scene descriptor's configuration plus a `problems` list naming anything that would block a build " +
    "(no descriptor, more than one, no valid spawn, missing PipelineManager).",
  { ...projectArg },
  async (a) => rpc("world.describe_scene", {}, { project: a.project })
);

tool(
  "world_validate",
  "Run the SDK's own pre-build validations and get errors, warnings and performance stats back as data.\n" +
    "There is no API for this — the validations exist only to paint the SDK panel's GUI — so this opens " +
    "the control panel, runs the checks into a throwaway element and reads the results out, including the " +
    "warnings and stats that have no public getter.",
  { ...projectArg, openPanel: z.boolean().optional().describe("Open the SDK panel if closed. Default true.") },
  async (a) => rpc("world.validate", { openPanel: a.openPanel ?? true }, { project: a.project, timeoutMs: 180_000 })
);

tool(
  "scene_apply_spec",
  "Create or update GameObjects and components from a declarative spec, in one call. Idempotent — objects " +
    "are keyed by hierarchy path, so re-applying converges instead of duplicating.\n" +
    "Prefer this over one-object-at-a-time editing: a world of any size otherwise costs hundreds of round " +
    "trips. Each object takes path, position/rotation/scale (rotation as 3 numbers is Euler), active, " +
    "layer, tag, primitive or prefab (honoured only at creation), and components[{type, fields}].\n" +
    "Warns when a component is not on VRChat's whitelist, because those are silently stripped at build. " +
    "Refuses UdonSharpBehaviours — use udon_attach, which also wires the backing UdonBehaviour.",
  {
    ...projectArg,
    objects: z.array(z.record(z.any())).optional(),
    delete: z.array(z.string()).optional().describe("Hierarchy paths to destroy."),
    dryRun: z.boolean().optional().describe("Report what would change without changing it."),
    saveScene: z.boolean().optional().describe("Default true."),
  },
  async (a) =>
    rpc(
      "scene.apply_spec",
      { objects: a.objects ?? [], delete: a.delete ?? [], dryRun: a.dryRun ?? false, saveScene: a.saveScene ?? true },
      { project: a.project, timeoutMs: 180_000 }
    )
);

tool(
  "scene_describe",
  "Hierarchy of the loaded scenes with each object's components, transform and layer.",
  {
    ...projectArg,
    root: z.string().optional().describe("Limit to this hierarchy path."),
    maxDepth: z.number().int().min(0).max(12).optional(),
    includeInactive: z.boolean().optional(),
  },
  async (a) =>
    rpc(
      "scene.describe",
      { root: a.root, maxDepth: a.maxDepth ?? 4, includeInactive: a.includeInactive ?? true },
      { project: a.project }
    )
);

tool(
  "scene_component_whitelist",
  "VRChat's allowed component types. Anything absent is stripped from the built world, so a scene can " +
    "look correct in the editor and lose behaviour once uploaded.",
  { ...projectArg, filter: z.string().optional() },
  async (a) => rpc("scene.component_whitelist", { filter: a.filter }, { project: a.project })
);

tool(
  "scene_spatial_audit",
  "Physics-based geometry checks: are spawns standing on something, how much of the scene has ground " +
    "beneath it, which objects float, interpenetrate, lack colliders, sit absurdly far from the origin or " +
    "have degenerate scale.\n" +
    "Findings are prompts to look, not verdicts — a deliberately floating lamp is not a bug. Pair with " +
    "scene_survey_shots, which covers composition rather than geometry.",
  {
    ...projectArg,
    gridStep: z.number().min(0.25).max(20).optional().describe("Floor-probe grid spacing in metres. Default 2."),
    maxObjects: z.number().int().min(1).max(50_000).optional(),
    floatThreshold: z.number().min(0).max(50).optional().describe("Metres of clearance that counts as floating. Default 0.35."),
  },
  async (a) =>
    rpc(
      "scene.spatial_audit",
      { gridStep: a.gridStep ?? 2, maxObjects: a.maxObjects ?? 4000, floatThreshold: a.floatThreshold ?? 0.35 },
      { project: a.project, timeoutMs: 180_000 }
    )
);

tool(
  "scene_survey_shots",
  "Render the scene to PNG files from the spawn points at player eye height, an orbit, and top-down, and " +
    "return their absolute paths for you to read back as images.\n" +
    "This is the only way to judge composition, facing and lighting — no amount of hierarchy inspection " +
    "will tell you a room reads as a room or that the lighting came out black.",
  {
    ...projectArg,
    mode: z.enum(["spawns", "orbit", "top", "all"]).optional(),
    width: z.number().int().min(128).max(3840).optional(),
    height: z.number().int().min(128).max(2160).optional(),
    yawSteps: z.number().int().min(1).max(12).optional().describe("Directions photographed per spawn. Default 4."),
    orbitCount: z.number().int().min(1).max(16).optional(),
    fieldOfView: z.number().min(20).max(140).optional(),
    label: z.string().optional().describe("Included in the filenames."),
  },
  async (a) =>
    rpc(
      "scene.survey_shots",
      {
        mode: a.mode ?? "all",
        width: a.width ?? 960,
        height: a.height ?? 540,
        yawSteps: a.yawSteps ?? 4,
        orbitCount: a.orbitCount ?? 4,
        fieldOfView: a.fieldOfView ?? 70,
        label: a.label ?? "survey",
      },
      { project: a.project, timeoutMs: 180_000 }
    )
);

// ---------------------------------------------------------------- verification

/**
 * Submits a job and polls until it finishes.
 *
 * Polling has to tolerate the editor going unresponsive: a build or a play-mode session occupies the main
 * thread for long stretches, so EDITOR_BUSY during the wait is expected rather than a failure.
 */
async function runJobAndWait(
  kind: string,
  params: Record<string, unknown>,
  options: { project?: string; waitMs?: number; pollMs?: number } = {}
): Promise<any> {
  const terminal = new Set(["succeeded", "failed", "cancelled", "interrupted"]);
  const job: any = await rpc("job.submit", { kind, params }, { project: options.project });
  const deadline = Date.now() + (options.waitMs ?? 900_000);
  let last: any = job;

  while (Date.now() < deadline) {
    try {
      last = await rpc("job.status", { id: job.id }, { project: options.project, timeoutMs: 20_000 });
      if (terminal.has(String(last.state))) return last;
    } catch (err) {
      if (err instanceof BridgeError && err.code !== "EDITOR_BUSY") throw err;
    }
    await new Promise((r) => setTimeout(r, options.pollMs ?? 1500));
  }
  return { ...last, timedOut: true, hint: "The job is still running; poll unity_job_status with its id." };
}

tool(
  "verify_scenario_schema",
  "The step types a ClientSim verification scenario accepts, with what each one does and does not prove.",
  { ...projectArg },
  async (a) => rpc("verify.scenario_schema", {}, { project: a.project })
);

tool(
  "verify_clientsim",
  "Run a behavioural scenario against the world in a real ClientSim play-mode session, and return a " +
    "per-step pass/fail report.\n" +
    "PROVES Udon program logic: interact entry points, event chains, variable state, objects ending up " +
    "active or not.\n" +
    "DOES NOT prove reachability — `interact` invokes the behaviour's entry point directly rather than " +
    "aiming a player at a collider, so it cannot catch a button behind a wall; use scene_spatial_audit for " +
    "geometry. DOES NOT prove networking — ClientSim is single-player, so ownership, late joiners and sync " +
    "need verify_multiclient.\n" +
    "Call verify_scenario_schema for the step list. The session disables domain reload on Play, which is " +
    "required and also makes the SDK emit known harmless errors; assert_no_errors reports those separately " +
    "under `ignored` instead of failing.",
  {
    ...projectArg,
    steps: z.array(z.record(z.any())).describe("Scenario steps, in order."),
    name: z.string().optional(),
    settings: z.record(z.any()).optional().describe("ClientSimSettings overrides for this run."),
    stopOnFailure: z.boolean().optional().describe("Default true."),
    waitMs: z.number().int().min(10_000).max(3_600_000).optional(),
  },
  async (a) =>
    runJobAndWait(
      "verify.clientsim",
      { steps: a.steps, name: a.name ?? "scenario", settings: a.settings ?? {}, stopOnFailure: a.stopOnFailure ?? true },
      { project: a.project, waitMs: a.waitMs ?? 900_000 }
    )
);

tool(
  "verify_build_local",
  "Build the open scene into a local .vrcw world bundle and report its path and size. This is the SDK's " +
    "real build pipeline, so it catches everything validation does not.\n" +
    "Upload is intentionally not available: the autonomy boundary for this toolchain is local Build & Test.",
  {
    ...projectArg,
    dryRun: z.boolean().optional().describe("Use the SDK's dry-run flag to smoke-test the pipeline without building."),
    waitMs: z.number().int().min(10_000).max(3_600_000).optional(),
  },
  async (a) => runJobAndWait("verify.build_local", { dryRun: a.dryRun ?? false }, { project: a.project, waitMs: a.waitMs ?? 1_800_000 })
);

tool(
  "world_build_status",
  "Active build target, which platform modules this editor has, the SDK dry-run flag, and the Build & Test client count.",
  { ...projectArg },
  async (a) => rpc("world.build_status", {}, { project: a.project })
);

tool(
  "world_set_dry_run",
  "Toggle the SDK's dry-run flag, which makes build and upload report success without doing the work.",
  { ...projectArg, enabled: z.boolean() },
  async (a) => rpc("world.set_dry_run", { enabled: a.enabled }, { project: a.project })
);

tool(
  "world_switch_platform",
  "Switch the active build target, for producing both a PC and a Quest/Android build of the same world. " +
    "This reloads the script domain, so any job in flight is interrupted and must be resubmitted.",
  { ...projectArg, platform: z.enum(["Windows", "Android", "iOS"]) },
  async (a) => rpc("world.switch_platform", { platform: a.platform }, { project: a.project, timeoutMs: 300_000 })
);

tool(
  "verify_multiclient_preflight",
  "What must be true before Build & Test can launch real VRChat clients, and which parts currently are. " +
    "Needs a VRChat client installed, a signed-in account, a world builder and a PC build target.",
  { ...projectArg },
  async (a) => rpc("verify.multiclient_preflight", {}, { project: a.project, timeoutMs: 120_000 })
);

tool(
  "verify_multiclient",
  "Build & Test with several real VRChat clients in one instance, then read their logs. This is the only " +
    "way to verify networking: ClientSim simulates none, so ownership, late-joiner state and sync " +
    "convergence are invisible to verify_clientsim.\n" +
    "Udon cannot send data out (VRCUrl is not constructible at runtime), so assert on telemetry the world " +
    "itself logs: Debug.Log lines of the form [AGENTTEL]{\"key\":\"value\"}, which this parses back out.",
  {
    ...projectArg,
    clients: z.number().int().min(1).max(8).optional().describe("Clients to launch. Default 2."),
    observeSeconds: z.number().int().min(5).max(1800).optional().describe("How long to let them run before reading logs. Default 60."),
    forceNoVR: z.boolean().optional().describe("Launch in desktop mode. Default true."),
    waitMs: z.number().int().min(10_000).max(3_600_000).optional(),
  },
  async (a) =>
    runJobAndWait(
      "verify.multiclient",
      { clients: a.clients ?? 2, observeSeconds: a.observeSeconds ?? 60, forceNoVR: a.forceNoVR ?? true },
      { project: a.project, waitMs: a.waitMs ?? 1_800_000 }
    )
);

tool(
  "verify_vrc_logs",
  "Parse the real VRChat client's logs for Udon exceptions, Debug.Log output and [AGENTTEL] telemetry. " +
    "The client must have been launched with --enable-udon-debug-logging or Udon lines are absent.",
  {
    ...projectArg,
    maxFiles: z.number().int().min(1).max(12).optional(),
    maxLines: z.number().int().min(100).max(50_000).optional(),
    sinceUtc: z.string().optional().describe("Ignore log files older than this ISO timestamp."),
    grep: z.string().optional(),
  },
  async (a) =>
    rpc(
      "verify.read_vrc_logs",
      { maxFiles: a.maxFiles ?? 2, maxLines: a.maxLines ?? 4000, sinceUtc: a.sinceUtc, grep: a.grep },
      { project: a.project, timeoutMs: 120_000 }
    )
);

tool(
  "verify_udon_exceptions",
  "Udon runtime exceptions from both sides: the Unity console (where UdonSharp's watcher maps a fault back " +
    "to the C# file and line) and the VRChat client logs.",
  { ...projectArg, sinceSeq: z.number().int().min(0).optional() },
  async (a) => rpc("verify.udon_exceptions", { sinceSeq: a.sinceSeq ?? 0 }, { project: a.project })
);

// ---------------------------------------------------------------- budget & assets

tool(
  "budget_report",
  "Per-platform content budget: geometry, materials, textures, lights, audio, VRChat components, and the " +
    "built bundle measured against VRChat's size limit.\n" +
    "The limits are read from the SDK, not hardcoded. Note what that reveals: VRChat enforces a bundle size " +
    "limit for mobile but *not* for PC, so on PC `compressedEnforced` is false and size is a performance " +
    "judgement rather than a gate. Errors in the report are things VRChat actually blocks; warnings are " +
    "recommendations, since worlds have no hard polygon or material limit.",
  {
    ...projectArg,
    platform: z.enum(["current", "Windows", "Android"]).optional(),
    maxObjects: z.number().int().min(1).max(200_000).optional(),
  },
  async (a) =>
    rpc("budget.report", { platform: a.platform ?? "current", maxObjects: a.maxObjects ?? 20_000 },
      { project: a.project, timeoutMs: 180_000 })
);

tool(
  "budget_snapshot",
  "Save a named budget report so a later change can be diffed against it.",
  { ...projectArg, name: z.string(), platform: z.enum(["current", "Windows", "Android"]).optional() },
  async (a) => rpc("budget.snapshot", { name: a.name, platform: a.platform ?? "current" }, { project: a.project, timeoutMs: 180_000 })
);

tool(
  "budget_diff",
  "Compare a saved snapshot against the scene now, reporting only the metrics that moved — so the cost of a " +
    "change is immediately visible rather than buried in unchanged numbers.",
  { ...projectArg, name: z.string(), platform: z.enum(["current", "Windows", "Android"]).optional() },
  async (a) => rpc("budget.diff", { name: a.name, platform: a.platform ?? "current" }, { project: a.project, timeoutMs: 180_000 })
);

tool(
  "budget_import_audit",
  "Report which assets do not match a platform's recommended import settings, changing nothing.\n" +
    "This is where a dual-platform world is won or lost. Android textures must be ASTC — the SDK reports an " +
    "error and refuses the build otherwise — and long audio clips left on DecompressOnLoad will exhaust " +
    "Quest's memory.",
  {
    ...projectArg,
    platform: z.enum(["current", "Windows", "Android"]).optional(),
    folder: z.string().optional(),
    maxAssets: z.number().int().min(1).max(50_000).optional(),
    maxTextureSize: z.number().int().min(32).max(8192).optional(),
  },
  async (a) =>
    rpc("budget.import_audit",
      { platform: a.platform ?? "current", folder: a.folder ?? "Assets", maxAssets: a.maxAssets ?? 2000, maxTextureSize: a.maxTextureSize },
      { project: a.project, timeoutMs: 300_000 })
);

tool(
  "budget_apply_import_presets",
  "Apply a platform's import settings to the assets that need them. Reimports every asset it touches, so " +
    "this runs as a job; audit first with budget_import_audit.",
  {
    ...projectArg,
    platform: z.enum(["current", "Windows", "Android"]).optional(),
    folder: z.string().optional(),
    maxAssets: z.number().int().min(1).max(50_000).optional(),
    maxTextureSize: z.number().int().min(32).max(8192).optional(),
    waitMs: z.number().int().min(10_000).max(3_600_000).optional(),
  },
  async (a) =>
    runJobAndWait("budget.apply_import_presets",
      { platform: a.platform ?? "current", folder: a.folder ?? "Assets", maxAssets: a.maxAssets ?? 2000, maxTextureSize: a.maxTextureSize },
      { project: a.project, waitMs: a.waitMs ?? 1_800_000 })
);

tool(
  "assets_inventory",
  "Assets by file type with sizes and totals — the quickest way to see what is actually consuming the project.",
  { ...projectArg, folder: z.string().optional(), maxAssets: z.number().int().min(1).max(100_000).optional() },
  async (a) => rpc("assets.inventory", { folder: a.folder ?? "Assets", maxAssets: a.maxAssets ?? 5000 }, { project: a.project, timeoutMs: 180_000 })
);

tool(
  "assets_unused",
  "Assets no scene can reach, with the size they would free. Reachability uses the same dependency walk the " +
    "build does. Scripts and editor-only assets are excluded deliberately: they are reached through code, so " +
    "this evidence cannot condemn them.",
  {
    ...projectArg,
    folder: z.string().optional(),
    scenes: z.array(z.string()).optional().describe("Scenes to treat as roots. Default: every scene in the project."),
    maxAssets: z.number().int().min(1).max(100_000).optional(),
  },
  async (a) =>
    rpc("assets.unused", { folder: a.folder ?? "Assets", scenes: a.scenes ?? [], maxAssets: a.maxAssets ?? 5000 },
      { project: a.project, timeoutMs: 300_000 })
);

tool(
  "assets_make_material",
  "Create or update a material. Properties the shader does not have are reported as skipped rather than " +
    "silently dropped.",
  {
    ...projectArg,
    path: z.string().describe("Asset path under Assets/, e.g. Assets/Materials/Glass.mat"),
    shader: z.string().optional().describe('Shader name. Default "Standard".'),
    textures: z.record(z.string()).optional().describe("Shader property to texture asset path."),
    colors: z.record(z.any()).optional().describe("Shader property to [r,g,b,a] or {r,g,b,a}."),
    floats: z.record(z.number()).optional(),
  },
  async (a) =>
    rpc("assets.make_material",
      { path: a.path, shader: a.shader ?? "Standard", textures: a.textures, colors: a.colors, floats: a.floats },
      { project: a.project, timeoutMs: 120_000 })
);

tool(
  "assets_import_file",
  "Copy an external file into the project and import it, recording where it came from.\n" +
    "`source` and `licence` are required, not optional. An agent that imports assets without recording " +
    "provenance produces a world nobody can later clear for distribution, and that cannot be reconstructed " +
    "after the fact.",
  {
    ...projectArg,
    sourcePath: z.string().describe("Absolute path of the file to bring in."),
    source: z.string().describe("Where it came from, e.g. \"Poly Haven\"."),
    licence: z.string().describe("Licence identifier, e.g. \"CC0-1.0\"."),
    sourceUrl: z.string().optional(),
    destFolder: z.string().optional().describe("Default Assets/Imported."),
    name: z.string().optional(),
  },
  async (a) =>
    rpc("assets.import_file",
      { sourcePath: a.sourcePath, source: a.source, licence: a.licence, sourceUrl: a.sourceUrl, destFolder: a.destFolder, name: a.name },
      { project: a.project, timeoutMs: 180_000 })
);

tool(
  "assets_ledger",
  "Read the asset provenance ledger, optionally regenerating ATTRIBUTION.md from it. Also reports ledger " +
    "entries whose asset has since been deleted, so the attribution file stays truthful.",
  { ...projectArg, writeAttribution: z.boolean().optional(), attributionPath: z.string().optional() },
  async (a) =>
    rpc("assets.ledger", { writeAttribution: a.writeAttribution ?? false, attributionPath: a.attributionPath },
      { project: a.project, timeoutMs: 120_000 })
);

tool(
  "geo_kinds",
  "The architecture geo_generate can build, with parameters and examples.",
  { ...projectArg },
  async (a) => rpc("geo.kinds", {}, { project: a.project })
);

tool(
  "geo_generate",
  "Generate architecture from parameters: a room (walls, floor, ceiling, doorways), a corridor between two " +
    "points, or stairs. Idempotent — regenerating replaces the previous children rather than stacking.\n" +
    "What this saves is the arithmetic, not the primitives. A wall with a doorway is three boxes whose sizes " +
    "and offsets all depend on each other, and getting one wrong leaves a gap a player walks through. " +
    "`size` is the room's interior; the floor is extended to pass under the walls.",
  {
    ...projectArg,
    kind: z.enum(["room", "corridor", "stairs"]),
    path: z.string().describe("Hierarchy path for the generated parent."),
    size: z.array(z.number()).optional().describe("room: interior [x,y,z]."),
    thickness: z.number().optional(),
    floor: z.boolean().optional(),
    ceiling: z.boolean().optional(),
    walls: z.array(z.enum(["north", "south", "east", "west"])).optional(),
    doors: z.array(z.record(z.any())).optional().describe("room: [{wall,width,height,offset}]."),
    from: z.array(z.number()).optional().describe("corridor start."),
    to: z.array(z.number()).optional().describe("corridor end."),
    width: z.number().optional(),
    height: z.number().optional(),
    steps: z.number().int().min(1).max(400).optional(),
    stepHeight: z.number().optional(),
    stepDepth: z.number().optional(),
    material: z.string().optional().describe("Material asset path applied to every piece."),
    saveScene: z.boolean().optional(),
  },
  async (a) => rpc("geo.generate", { ...a, project: undefined }, { project: a.project, timeoutMs: 180_000 })
);

// ---------------------------------------------------------------- udon lint (no Unity involved)

/**
 * Runs the standalone Roslyn linter.
 *
 * Deliberately out-of-process and Unity-free: getting a diagnostic from the real UdonSharp compiler costs a
 * file write, an asset refresh, a C# compile and a domain reload. Most UdonSharp violations are visible in
 * the syntax tree, so this turns that loop from tens of seconds into milliseconds.
 */
function runAnalyzer(paths: string[]): Promise<unknown> {
  const here = dirname(fileURLToPath(import.meta.url));
  const root = join(here, "..", "..", "udon-analyzer");
  const exe = join(root, "bin", "Release", "net8.0", process.platform === "win32" ? "udon-analyzer.exe" : "udon-analyzer");

  if (!existsSync(exe)) {
    return Promise.reject(
      new Error(
        `The Udon analyzer is not built. Run:\n  dotnet build -c Release "${join(root, "UdonAnalyzer.csproj")}"\n` +
          `Expected the binary at ${exe}`
      )
    );
  }

  return new Promise((resolve, reject) => {
    const child = spawn(exe, ["--json", ...paths], { stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (c) => (stdout += c.toString("utf8")));
    child.stderr.on("data", (c) => (stderr += c.toString("utf8")));
    child.on("error", reject);
    child.on("close", (code) => {
      // Exit code 1 means findings, which is a successful run with a useful answer; 2 means bad invocation.
      if (code === 2 || (!stdout.trim() && code !== 0)) {
        reject(new Error(`analyzer exited ${code}: ${stderr || "no output"}`));
        return;
      }
      try {
        resolve({ ...JSON.parse(stdout), exitCode: code, stderr: stderr || undefined });
      } catch (err) {
        reject(new Error(`could not parse analyzer output: ${(err as Error).message}\n${stdout.slice(0, 500)}`));
      }
    });
  });
}

tool(
  "udon_whitelist_query",
  "Ask whether a type, member or event exists in Udon at all, using the SDK's own node registry.\n" +
    "Use this instead of guessing. Udon exposes a hand-curated subset of Unity, so an API being absent is a " +
    "real answer rather than a spelling problem — and guessing costs a full write/compile/reload cycle per " +
    "attempt. Names come back in Udon's mangled form: Type.__method__ParamTypes__ReturnType.",
  {
    ...projectArg,
    query: z.string().describe('Substring to search for, e.g. "Networking.__SetOwner" or "Instantiate".'),
    kind: z.enum(["any", "event"]).optional().describe('"event" searches only Udon events.'),
    limit: z.number().int().min(1).max(500).optional(),
  },
  async (a) =>
    rpc("udon.whitelist_query", { query: a.query, kind: a.kind ?? "any", limit: a.limit ?? 40 },
      { project: a.project, timeoutMs: 120_000 })
);

tool(
  "udon_syncable",
  "Whether a type can be [UdonSynced], and whether it supports linear or smooth interpolation. Answered by " +
    "calling VRC.Udon.UdonNetworkTypes, so it is authoritative rather than remembered. Note that syncable " +
    "arrays additionally require the behaviour to use Manual sync.",
  { ...projectArg, type: z.string().describe('Full type name, e.g. "UnityEngine.Vector3".') },
  async (a) => rpc("udon.syncable", { type: a.type }, { project: a.project })
);

tool(
  "udon_export_whitelist",
  "Dump Udon's whole node registry and event list to a JSON file, generated from the installed SDK. Useful " +
    "as an offline reference that cannot drift out of date the way hand-written documentation does.",
  {
    ...projectArg,
    outputPath: z.string().optional().describe("Default <project>/.agentic/udon-whitelist.json."),
    waitMs: z.number().int().min(10_000).max(1_800_000).optional(),
  },
  async (a) => runJobAndWait("udon.export_whitelist", { outputPath: a.outputPath }, { project: a.project, waitMs: a.waitMs ?? 600_000 })
);

tool(
  "udon_lint",
  "Check UdonSharp sources for constructs UdonSharp rejects — without starting Unity, in milliseconds.\n" +
    "Run this before udon_compile on every edit. Getting the same answer from the real compiler costs a file " +
    "write, an asset refresh, a C# compile and a domain reload, and most violations (try/catch, LINQ, " +
    "lambdas, List<T>, static fields, multidimensional arrays, unsyncable [UdonSynced] types, arrays synced " +
    "without Manual mode) are visible in the syntax alone.\n" +
    "It is not a substitute: it has no symbol resolution, so it cannot tell whether a particular API is " +
    "exposed to Udon. udon_compile remains the authority.",
  {
    paths: z
      .array(z.string())
      .describe("Files or directories to analyse. Absolute paths, or paths relative to the working directory."),
  },
  async (a) => runAnalyzer(a.paths)
);

// ---------------------------------------------------------------- checkpointing

tool(
  "project_status",
  "Version-control state of the Unity project: whether it is tracked, what is dirty, whether git-lfs is " +
    "available, and whether .gitignore excludes Unity's generated caches.",
  { projectPath: z.string().describe("Unity project root.") },
  async (a) => projectGit.status(a.projectPath)
);

tool(
  "project_checkpoint",
  "Commit the project's current state so a later change can be undone.\n" +
    "Do this before any risky step. Unity scenes are huge generated YAML files: an agent cannot merge one, " +
    "cannot reliably hand-undo a bad scene edit, and Unity's own undo stack does not survive a domain reload, " +
    "so a commit is the only workable safety net. Also ensures .gitignore excludes Library/ and friends, " +
    "which would otherwise add gigabytes of generated cache.",
  {
    projectPath: z.string(),
    message: z.string().describe("What this checkpoint represents."),
    init: z.boolean().optional().describe("Initialise a repository if the project is not tracked yet."),
  },
  async (a) => projectGit.checkpoint(a.projectPath, a.message, a.init ?? false)
);

tool(
  "project_checkpoints",
  "Recent checkpoints, newest first, with the short sha to roll back to.",
  { projectPath: z.string(), limit: z.number().int().min(1).max(200).optional() },
  async (a) => projectGit.checkpoints(a.projectPath, a.limit ?? 20)
);

tool(
  "project_rollback",
  "Hard-reset the project to a checkpoint. Requires confirm:true.\n" +
    "Destructive to the working tree, but not to your work: the current state is committed as a safety " +
    "checkpoint first, so a rollback made in error can itself be rolled back, and the response says how.\n" +
    "Afterwards the editor still holds the old scene in memory, and rewriting scene files under a running " +
    "editor raises Unity's \"modified externally\" dialog — run the supervisor with --auto-dismiss-dialogs " +
    "true during rollbacks.",
  {
    projectPath: z.string(),
    target: z.string().describe("Checkpoint sha, or any git revision such as HEAD~1."),
    confirm: z.boolean().describe("Must be true. Guards against discarding the working tree by accident."),
  },
  async (a) => projectGit.rollback(a.projectPath, a.target, a.confirm === true)
);

// ---------------------------------------------------------------- boot

async function main(): Promise<void> {
  await server.connect(new StdioServerTransport());
}

main().catch((err) => {
  // stdout is the MCP channel; diagnostics must go to stderr or they corrupt the protocol stream.
  process.stderr.write(`vrcw-mcp failed to start: ${(err as Error).stack ?? err}\n`);
  process.exit(1);
});
