#!/usr/bin/env node
/**
 * The bundled generic Unity MCP layer (CoplayDev, MIT, vendored under third_party/unity-mcp).
 *
 * That half is here so an agent can reach the parts of Unity our own bridge deliberately does not
 * reimplement — GI baking, NavMesh, prefabs, the Test Runner, profiling. "Out of the box" is the whole claim
 * being made about it, so this suite checks the things that would quietly break that claim:
 *
 *   - the vendored tree is present, and still under a licence that permits redistributing it
 *   - the Unity package is installed, compiled, and its auto-start pref is on (it defaults to off)
 *   - telemetry is off on *both* halves — the Python server reads only environment variables, so the
 *     editor-launched server is a separate case from the one an MCP client launches
 *   - its server really answers over stdio, and really reaches this editor
 *   - and ours is unaffected by it sharing the editor
 *
 * Needs uv for the server checks and a running editor for the reachability ones; both report SKIP rather
 * than failing, because neither absence is a defect in this code.
 */
import { spawn, spawnSync } from "node:child_process";
import { connect } from "node:net";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { rpc } from "../packages/vrcw-mcp/dist/bridge.js";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..");
const vendorDir = join(repo, "third_party", "unity-mcp");
const serverDir = join(vendorDir, "Server");

let passed = 0;
let failed = 0;
let skipped = 0;

async function check(name, fn) {
  try {
    const detail = await fn();
    if (detail && String(detail).startsWith("SKIP")) {
      skipped++;
      console.log(`SKIP  ${name}`);
      console.log(`      ${String(detail).slice(5).trim()}`);
      return;
    }
    passed++;
    console.log(`PASS  ${name}`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 4).join("\n      ")}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

// ---------------------------------------------------------------- the vendored tree

await check("the vendored package is present", async () => {
  const manifest = join(vendorDir, "MCPForUnity", "package.json");
  assert(existsSync(manifest), `${manifest} is missing`);
  const parsed = JSON.parse(readFileSync(manifest, "utf8"));
  assert(parsed.name === "com.coplaydev.unity-mcp", `unexpected package name: ${parsed.name}`);
  assert(existsSync(serverDir), `${serverDir} is missing`);
  return `${parsed.name} ${parsed.version}`;
});

await check("its licence still permits redistribution", async () => {
  const licence = readFileSync(join(vendorDir, "LICENSE"), "utf8");
  assert(/MIT License/i.test(licence), "the LICENSE file is no longer an MIT licence");
  assert(/Permission is hereby granted, free of charge/i.test(licence), "the MIT grant clause is missing");
  const holder = licence.match(/Copyright \(c\)\s*(.+)/i)?.[1]?.trim();
  assert(holder, "no copyright holder named in the licence");
  return `MIT, ${holder}`;
});

await check("the vendoring is documented and the pinned tag matches what is here", async () => {
  const notes = readFileSync(join(vendorDir, "VENDORED.md"), "utf8");
  const tag = notes.match(/tag \*\*(v[^*]+)\*\*/)?.[1];
  assert(tag, "VENDORED.md does not record a pinned upstream tag");
  const version = JSON.parse(readFileSync(join(vendorDir, "MCPForUnity", "package.json"), "utf8")).version;
  // A tag of v10.2.0 should be vendoring package version 10.2.0. Drift here means the tree was replaced
  // without the notes being updated, which makes the provenance claim unverifiable.
  assert(tag.replace(/^v/, "") === version, `VENDORED.md pins ${tag} but the package here is ${version}`);
  assert(/MIT/.test(notes), "VENDORED.md does not state the licence");
  return `pinned at ${tag}, matching the vendored ${version}`;
});

// ---------------------------------------------------------------- MCP client configuration

await check("both servers are wired into .mcp.json, with telemetry opted out", async () => {
  const file = join(repo, ".mcp.json");
  assert(existsSync(file), `${file} is missing — run: node scripts/install.mjs --project <project>`);
  const config = JSON.parse(readFileSync(file, "utf8"));
  const servers = config.mcpServers ?? {};
  assert(servers.vrcw, "no vrcw server entry");

  const companion = servers["unity-generic"];
  if (!companion) return "SKIP .mcp.json has no unity-generic entry (the installer omits it when uv is absent)";

  const env = companion.env ?? {};
  assert(env.DISABLE_TELEMETRY === "1", "DISABLE_TELEMETRY is not set on the companion entry");
  assert(env.UNITY_MCP_DISABLE_TELEMETRY === "1", "UNITY_MCP_DISABLE_TELEMETRY is not set");
  const args = String(companion.args?.join(" ") ?? "");
  assert(args.includes("third_party/unity-mcp/Server"), "the companion entry does not point at the vendored server");
  // Without --no-sync, launching this server fights the one the Unity package started from the same venv:
  // uv tries to replace an executable that is open, and on Windows that fails and the server never answers.
  assert(args.includes("--no-sync"), "the companion entry syncs the shared venv on every launch");
  return `vrcw + unity-generic (${Object.keys(env).length} opt-out variables)`;
});

// ---------------------------------------------------------------- the editor side

let bridgeMethods = 0;
let companionStatus = null;

await check("our bridge is reachable, and reports the companion's state", async () => {
  const health = await rpc("bridge.health", {}, { timeoutMs: 20_000 });
  bridgeMethods = health.methodCount;
  companionStatus = await rpc("companion.status", {}, { timeoutMs: 60_000 });
  assert(companionStatus.packageName === "com.coplaydev.unity-mcp", "companion.status named the wrong package");
  return `${bridgeMethods} methods on port ${health.port}; companion installed=${companionStatus.installed}`;
});

await check("the companion package is installed into the project and compiled", async () => {
  if (!companionStatus) return "SKIP no editor is running";
  assert(companionStatus.installed, `not installed: ${companionStatus.nextSteps?.join("; ")}`);
  // Unity only rescans Packages/ at startup, so "installed but not compiled" is a real and confusing state.
  assert(companionStatus.compiled, `installed but not compiled: ${companionStatus.nextSteps?.join("; ")}`);
  return `assemblies: ${companionStatus.assembliesLoaded?.join(", ")}`;
});

await check("its bridge is enabled and serving, without anyone having turned it on", async () => {
  if (!companionStatus) return "SKIP no editor is running";
  assert(companionStatus.autoStartOnLoad, "MCPForUnity.AutoStartOnLoad is false, so its server cannot find the editor");
  assert(companionStatus.bridge, `no status file, so its bridge is not serving: ${companionStatus.nextSteps?.join("; ")}`);
  assert(companionStatus.ready, "companion.status does not consider the companion ready");
  return `port ${companionStatus.bridge.unity_port}, reason "${companionStatus.bridge.reason}", project ${companionStatus.bridge.project_name}`;
});

await check("its liveness is a live check, not a leftover file", async () => {
  if (!companionStatus) return "SKIP no editor is running";
  assert(typeof companionStatus.serving === "boolean", "companion.status does not report serving");
  assert(!companionStatus.ready || companionStatus.serving, "ready is true while serving is false");

  // The regression this guards: the companion writes a status file naming its port and only rewrites it on a
  // state change, so the file outlives the process. After one machine restart it advertised a port nothing
  // had listened on since the previous day, the status said "ready", and every call to the generic server
  // came back "No Unity Editor instances found". Connecting is the only honest test, so the test connects.
  const port = companionStatus.bridge?.unity_port;
  if (!companionStatus.serving) return `reported as not serving${port ? ` on port ${port}` : ""}, which is a real answer`;

  assert(port > 0, `serving is true but no port was reported: ${JSON.stringify(companionStatus.bridge)}`);
  const reachable = await new Promise((resolve) => {
    const socket = connect({ host: "127.0.0.1", port, timeout: 2000 });
    socket.on("connect", () => { socket.destroy(); resolve(true); });
    socket.on("error", () => resolve(false));
    socket.on("timeout", () => { socket.destroy(); resolve(false); });
  });
  assert(reachable, `companion.status says it is serving, but nothing answers on port ${port}`);
  return `port ${port} accepted a connection, so "serving" means serving`;
});

await check("telemetry is off on the editor side, pref and environment both", async () => {
  if (!companionStatus) return "SKIP no editor is running";
  assert(companionStatus.telemetryDisabled, "MCPForUnity.TelemetryDisabled is not set in EditorPrefs");
  // The environment one matters because the Unity package launches a server of its own, and that child
  // inherits the editor's environment rather than anything an MCP client sets.
  assert(companionStatus.telemetryOptOutEnv, "DISABLE_TELEMETRY is not set in the editor process");
  return "EditorPref set, and DISABLE_TELEMETRY=1 in the editor process so its child servers inherit it";
});

// ---------------------------------------------------------------- the server over stdio

/**
 * Spawns a command that may be a shim.
 *
 * uv is a .cmd shim on Windows and needs a shell there; passing an args array alongside shell:true is
 * deprecated (DEP0190), so the command is quoted into one string instead. Elsewhere it is a real executable.
 */
function spawnTool(cmd, args, opts = {}) {
  if (process.platform !== "win32") return spawn(cmd, args, opts);
  const quote = (v) => (/[\s&|<>^"]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v);
  return spawn([cmd, ...args].map(quote).join(" "), { ...opts, shell: true });
}

/**
 * How .mcp.json says to launch the companion server, so this suite tests the configuration that is actually
 * used rather than a command that happens to resemble it. The two drifted once already: the config gained a
 * flag the test did not have, and the test kept passing while the real thing hung.
 */
function configuredLaunch() {
  const fallback = {
    command: "uv",
    args: ["run", "--directory", serverDir, "--frozen", "--no-sync", "mcp-for-unity"],
    env: { DISABLE_TELEMETRY: "1", UNITY_MCP_DISABLE_TELEMETRY: "1", MCP_DISABLE_TELEMETRY: "1" },
  };
  try {
    const entry = JSON.parse(readFileSync(join(repo, ".mcp.json"), "utf8")).mcpServers?.["unity-generic"];
    if (!entry?.command || !Array.isArray(entry.args)) return fallback;
    return { command: entry.command, args: entry.args, env: entry.env ?? {} };
  } catch {
    return fallback;
  }
}

/** Minimal MCP stdio client. The companion's server is Python, so this cannot reuse our own harness. */
function startCompanionServer() {
  const launch = configuredLaunch();
  const child = spawnTool(launch.command, launch.args, {
    stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env, ...launch.env },
  });

  const pending = new Map();
  const stderr = [];
  let buffer = "";

  child.stdout.on("data", (chunk) => {
    buffer += chunk.toString("utf8");
    let newline;
    while ((newline = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      if (!line) continue;
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        continue;
      }
      if (message.id !== undefined && pending.has(message.id)) {
        pending.get(message.id)(message);
        pending.delete(message.id);
      }
    }
  });
  child.stderr.on("data", (c) => stderr.push(c.toString("utf8")));

  let nextId = 1;
  function request(method, params, timeoutMs = 180_000) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, resolve);
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
      setTimeout(() => {
        if (!pending.has(id)) return;
        pending.delete(id);
        reject(new Error(`timed out on ${method}. stderr tail: ${stderr.join("").slice(-600)}`));
      }, timeoutMs);
    });
  }

  function notify(method, params) {
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");
  }

  return { child, request, notify, stderr };
}

function uvPresent() {
  const probe = spawnTool("uv", ["--version"], { stdio: "ignore" });
  return new Promise((resolve) => {
    probe.on("error", () => resolve(false));
    probe.on("close", (code) => resolve(code === 0));
  });
}

const haveUv = await uvPresent();
let server = null;
let tools = [];

if (haveUv) server = startCompanionServer();

await check("its server completes an MCP handshake", async () => {
  if (!server) return "SKIP uv is not installed, so the generic server cannot run";
  const init = await server.request("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "vrcw-companion-test", version: "0" },
  });
  assert(init.result?.serverInfo, `no serverInfo in the initialize reply: ${JSON.stringify(init).slice(0, 300)}`);
  server.notify("notifications/initialized", {});
  const launch = configuredLaunch();
  return `${init.result.serverInfo.name} ${init.result.serverInfo.version}, launched as configured: ` +
    `${launch.command} ${launch.args.filter((a) => !a.includes("/")).join(" ")}`;
});

await check("it exposes the generic editor surface our bridge leaves alone", async () => {
  if (!server) return "SKIP uv is not installed";
  const list = await server.request("tools/list", {});
  tools = list.result?.tools ?? [];
  assert(tools.length >= 20, `only ${tools.length} tools; the vendored server looks broken`);

  const names = tools.map((t) => t.name);
  // Named individually because these are the reason the package is bundled at all: each is a capability we
  // decided not to reimplement. If an upstream release renames them, that is worth failing over.
  const wanted = ["manage_scene", "manage_gameobject", "manage_asset", "manage_editor", "read_console"];
  const missing = wanted.filter((n) => !names.includes(n));
  assert(missing.length === 0, `missing: ${missing.join(", ")}`);
  return `${tools.length} tools, including ${wanted.join(", ")}`;
});

await check("its own telemetry check reports telemetry off", async () => {
  if (!server) return "SKIP uv is not installed";
  if (!tools.some((t) => t.name === "manage_editor")) return "SKIP manage_editor is not exposed";
  const call = await server.request("tools/call", { name: "manage_editor", arguments: { action: "telemetry_status" } });
  const text = call.result?.content?.[0]?.text ?? "";
  let parsed = null;
  try {
    parsed = JSON.parse(text);
  } catch {
    /* the reply is not JSON, which the assertion below reports */
  }
  assert(parsed, `could not parse the reply: ${text.slice(0, 200)}`);
  assert(parsed.telemetry_enabled === false, `telemetry is enabled: ${text.slice(0, 200)}`);
  return "telemetry_enabled=false, as reported by the server itself";
});

await check("it reaches this editor, not just its own process", async () => {
  if (!server) return "SKIP uv is not installed";
  if (!companionStatus?.ready) return "SKIP the companion's bridge is not serving";
  const call = await server.request("tools/call", {
    name: "manage_scene",
    arguments: { action: "get_hierarchy" },
  });
  const text = call.result?.content?.[0]?.text ?? "";
  assert(call.result?.isError !== true, `the call failed: ${text.slice(0, 300)}`);
  let parsed = null;
  try {
    parsed = JSON.parse(text);
  } catch {
    /* reported below */
  }
  assert(parsed?.success, `unsuccessful reply: ${text.slice(0, 300)}`);
  return String(parsed.message ?? "").slice(0, 160);
});

// ---------------------------------------------------------------- coexistence

await check("our own bridge is unaffected by sharing the editor", async () => {
  const health = await rpc("bridge.health", {}, { timeoutMs: 20_000 });
  assert(health.methodCount === bridgeMethods, `method count moved: ${bridgeMethods} -> ${health.methodCount}`);
  assert(health.secondsSincePump < 10, `the main-thread pump has stalled for ${health.secondsSincePump}s`);
  // The two bridges listen on different ports by construction: ours is ephemeral and registry-published,
  // theirs is 6400 by default. A collision would show up as one of them being unreachable.
  if (companionStatus?.bridge?.unity_port) {
    assert(health.port !== companionStatus.bridge.unity_port, `both bridges claim port ${health.port}`);
  }
  const udon = await rpc("udon.status", {}, { timeoutMs: 60_000 });
  assert(typeof udon === "object", "udon.status returned nothing");
  return `${health.methodCount} methods, pump ${Math.round(health.secondsSincePump * 1000)}ms ago, UdonSharp still answering`;
});

/**
 * Stops the companion server and everything it started.
 *
 * On Windows `uv` is reached through a shell, so killing the child kills cmd.exe and leaves the Python
 * server behind holding the inherited pipes — which keeps this process alive long after the last check. The
 * tree kill plus destroying the streams is what actually lets the suite end, and the unref'd timer is the
 * backstop: it forces an exit only if some handle still has not closed, and by then the HTTP client's
 * pooled sockets are long gone.
 */
function stopServer(child) {
  try {
    child.stdin?.destroy();
    child.stdout?.destroy();
    child.stderr?.destroy();
  } catch {
    /* already closed */
  }
  if (process.platform === "win32" && child.pid) {
    spawnSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" });
  }
  try {
    child.kill();
  } catch {
    /* already gone */
  }
}

if (server) stopServer(server.child);

console.log(`\n${passed}/${passed + failed} checks passed${skipped ? `, ${skipped} skipped` : ""}`);
process.exitCode = failed > 0 ? 1 : 0;
setTimeout(() => process.exit(process.exitCode ?? 0), 2000).unref();
