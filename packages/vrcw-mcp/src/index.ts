#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { listInstances } from "./registry.js";
import { ping, rpc, BridgeError } from "./bridge.js";

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

// ---------------------------------------------------------------- boot

async function main(): Promise<void> {
  await server.connect(new StdioServerTransport());
}

main().catch((err) => {
  // stdout is the MCP channel; diagnostics must go to stderr or they corrupt the protocol stream.
  process.stderr.write(`vrcw-mcp failed to start: ${(err as Error).stack ?? err}\n`);
  process.exit(1);
});
