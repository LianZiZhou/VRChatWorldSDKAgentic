#!/usr/bin/env node
/**
 * One command that turns a checkout of this repository plus a VRChat Unity project into a working
 * autonomous setup.
 *
 *   node scripts/install.mjs --project "E:/Unity/WorldTest"
 *
 * Every step is idempotent, reports what it did, and explains itself when it cannot proceed. Nothing here
 * needs network access if the repository's committed artifacts are present.
 *
 * What it installs, and why each piece is here:
 *
 *   com.agentic.vrcw          the editor bridge — the VRChat-specific half
 *   com.coplaydev.unity-mcp   the bundled generic Unity MCP package (MIT, vendored under third_party/)
 *   bundled/ *.mjs            the MCP server and the supervisor, dependencies already inlined
 *   udon-analyzer             the out-of-Unity linter
 *   .mcp.json                 both servers wired up, with the companion's telemetry switched off
 *
 * Two steps exist purely so that nobody has to know about them. Unity only rescans Packages/ at startup, so
 * a package added to a running editor stays invisible until PackageManager.Client.Resolve() is called; and
 * the companion's bridge is gated behind an EditorPref that defaults to false, so it ships installed but
 * mute. Both are handled here and, for a freshly opened editor, by the bridge itself.
 */
import { spawnSync } from "node:child_process";
import {
  cpSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  realpathSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { hashSources } from "./source-hash.mjs";

const repo = resolve(join(dirname(fileURLToPath(import.meta.url)), ".."));

// ---------------------------------------------------------------- arguments

const flags = new Map();
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  const arg = argv[i];
  if (!arg.startsWith("--")) continue;
  const name = arg.slice(2);
  const next = argv[i + 1];
  if (next && !next.startsWith("--")) {
    flags.set(name, next);
    i++;
  } else {
    flags.set(name, "true");
  }
}

if (flags.has("help")) {
  process.stdout.write(
    [
      "Usage: node scripts/install.mjs --project <unity project dir> [options]",
      "",
      "  --project <dir>      the VRChat Unity project to install into",
      "  --copy               copy the Unity packages instead of linking them (links keep edits live)",
      "  --mcp-config <file>  where to write the MCP client config (default: <repo>/.mcp.json)",
      "  --no-mcp-config      leave MCP client configuration alone",
      "  --skip-server-deps   do not run `uv sync` for the bundled Python server",
      "  --skip-build         use only what is committed; build nothing",
      "  --json               machine-readable report on stdout",
      "",
    ].join("\n")
  );
  process.exit(0);
}

const options = {
  project: flags.get("project") ?? "",
  copy: flags.get("copy") === "true",
  mcpConfig: flags.get("no-mcp-config") === "true" ? null : (flags.get("mcp-config") ?? join(repo, ".mcp.json")),
  skipServerDeps: flags.get("skip-server-deps") === "true",
  skipBuild: flags.get("skip-build") === "true",
  json: flags.get("json") === "true",
};

// ---------------------------------------------------------------- reporting

const steps = [];

/** ok = did it or verified it; skip = not applicable; warn = usable but degraded; fail = incomplete. */
function step(name, status, detail, hint) {
  steps.push({ name, status, detail, hint });
  if (options.json) return;
  const mark = { ok: "  ok  ", skip: " skip ", warn: " warn ", fail: " FAIL " }[status];
  process.stdout.write(`[${mark}] ${name}\n`);
  if (detail) process.stdout.write(`         ${detail}\n`);
  if (hint) process.stdout.write(`         -> ${hint}\n`);
}

function finish() {
  const failed = steps.filter((s) => s.status === "fail");
  const warned = steps.filter((s) => s.status === "warn");

  if (options.json) {
    process.stdout.write(JSON.stringify({ repo, project, steps, ok: failed.length === 0 }, null, 2) + "\n");
    return done(failed.length ? 1 : 0);
  }

  process.stdout.write("\n");
  if (failed.length) {
    process.stdout.write(`Install incomplete: ${failed.length} step(s) failed.\n`);
    return done(1);
  }

  process.stdout.write(`Installed${warned.length ? ` with ${warned.length} warning(s)` : ""}.\n\n`);
  process.stdout.write("One thing still needs a human, once per Unity project:\n");
  process.stdout.write("  sign in to VRChat in that project's SDK control panel. Even a local Build & Test\n");
  process.stdout.write("  needs it, because the panel only registers builders while a user is logged in.\n\n");
  process.stdout.write("Then:\n");
  process.stdout.write(`  node "${join(repo, "bundled", "vrcw-supervisor.mjs")}" watch --project "${project}" --auto-dismiss-dialogs true\n`);
  process.stdout.write(`  node "${join(repo, "tests", "e2e-world.mjs")}"\n`);
  return done(0);
}

/**
 * Ends the run without process.exit() where it can be avoided.
 *
 * The HTTP client that talks to a live editor keeps a pooled socket open briefly after the last call, and
 * exiting while that handle is closing makes Node abort inside libuv with an assertion instead of
 * returning a status. Setting the code and letting the loop drain costs a fraction of a second and always
 * reports the real outcome.
 */
function done(code) {
  process.exitCode = code;
}

/** Report and stop now. Used before anything has opened a socket, so exiting outright is safe. */
function bail() {
  finish();
  process.exit(process.exitCode ?? 1);
}

/**
 * Quotes one argument for cmd.exe. Only Windows needs this: see run().
 */
function winQuote(value) {
  return /[\s&|<>^"]/.test(value) ? '"' + value.replace(/"/g, '""') + '"' : value;
}

function run(cmd, args, opts = {}) {
  // npm, uv and dotnet are .cmd shims on Windows and cannot be executed without a shell, so there the
  // command is assembled into one quoted string — Node deprecates passing an args array alongside
  // shell:true (DEP0190). Everywhere else they are real executables and no shell is involved.
  const windows = process.platform === "win32";
  const result = windows
    ? spawnSync([cmd, ...args].map(winQuote).join(" "), {
        encoding: "utf8",
        timeout: opts.timeout ?? 600_000,
        cwd: opts.cwd ?? repo,
        shell: true,
      })
    : spawnSync(cmd, args, { encoding: "utf8", timeout: opts.timeout ?? 600_000, cwd: opts.cwd ?? repo });
  const out = `${result.stdout ?? ""}${result.stderr ?? ""}`.trim();
  return { ok: !result.error && result.status === 0, status: result.status, out, error: result.error };
}

function have(cmd, args = ["--version"]) {
  const probe = run(cmd, args, { timeout: 60_000 });
  return probe.ok ? probe.out.split(/\r?\n/)[0].trim() : null;
}

// ---------------------------------------------------------------- 1. the checkout

{
  const manifest = JSON.parse(readFileSync(join(repo, "packages", "unity-bridge", "package.json"), "utf8"));
  step("checkout", "ok", `${repo} (com.agentic.vrcw ${manifest.version})`);
}

// ---------------------------------------------------------------- 2. the Unity project

function knownProjects() {
  const dir = join(process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local"), "AgenticVrcw", "instances");
  if (!existsSync(dir)) return [];
  const found = [];
  for (const file of readdirSync(dir)) {
    try {
      const info = JSON.parse(readFileSync(join(dir, file), "utf8"));
      if (info.projectPath) found.push(String(info.projectPath));
    } catch {
      // A half-written registry file is not worth failing an install over.
    }
  }
  return found;
}

let project = options.project ? resolve(options.project) : "";
if (!project) {
  const known = knownProjects();
  if (known.length === 1) project = resolve(known[0]);
}

if (!project) {
  step("project", "fail", "no --project given, and no single running editor to infer one from",
    'pass --project "<path to the Unity project folder>"');
  bail();
}

if (!existsSync(join(project, "Packages", "manifest.json"))) {
  step("project", "fail", `${project} has no Packages/manifest.json, so it is not a Unity project`);
  bail();
}

{
  const hasWorlds = existsSync(join(project, "Packages", "com.vrchat.worlds"));
  const versionFile = join(project, "ProjectSettings", "ProjectVersion.txt");
  const version = existsSync(versionFile)
    ? (readFileSync(versionFile, "utf8").match(/m_EditorVersion:\s*(\S+)/)?.[1] ?? "unknown")
    : "unknown";

  // 2022.3.22f1 and 2022.3.22f1c1 are different editors, and VRChat does not support the China builds.
  // Worth saying now rather than at build time.
  if (!hasWorlds) {
    step("project", "warn", `${project} (Unity ${version}) has no com.vrchat.worlds package`,
      "the bridge installs fine, but the VRChat-specific methods will report the SDK as missing");
  } else if (/c\d+$/.test(version)) {
    step("project", "warn", `${project} (Unity ${version} — a Unity China build)`,
      "VRChat does not support the China editors; open this project with the international build of the same version");
  } else {
    step("project", "ok", `${project} (Unity ${version}, VRChat Worlds SDK present)`);
  }
}

// ---------------------------------------------------------------- 3 & 4. the Unity packages

function installPackage(name, source, label) {
  const target = join(project, "Packages", name);

  if (existsSync(target)) {
    let link = null;
    try {
      link = lstatSync(target).isSymbolicLink() ? realpathSync(target) : null;
    } catch {
      link = null;
    }
    if (link && resolve(link) === resolve(source)) {
      step(label, "ok", `already linked: ${target} -> ${source}`);
      return;
    }
    if (link) {
      step(label, "warn", `${target} is linked to ${link}, not to ${source}`,
        "remove that link and re-run if you meant to use this checkout");
      return;
    }
    if (options.copy) {
      cpSync(source, target, { recursive: true });
      step(label, "ok", `refreshed the copy at ${target}`);
      return;
    }
    step(label, "warn", `${target} already exists as a real directory`,
      "delete it and re-run to link this checkout, or pass --copy to refresh it in place");
    return;
  }

  mkdirSync(dirname(target), { recursive: true });

  if (options.copy) {
    cpSync(source, target, { recursive: true });
    step(label, "ok", `copied ${source} -> ${target}`);
    return;
  }

  try {
    // A junction rather than a copy, so editing the package in the checkout edits what Unity loads.
    symlinkSync(source, target, process.platform === "win32" ? "junction" : "dir");
    step(label, "ok", `linked ${target} -> ${source}`);
  } catch (err) {
    step(label, "fail", `could not link ${target}: ${String(err.message ?? err)}`,
      "pass --copy to copy the package instead (linking on Windows can need developer mode or an elevated shell)");
  }
}

installPackage("com.agentic.vrcw", join(repo, "packages", "unity-bridge"), "unity package: com.agentic.vrcw");
installPackage(
  "com.coplaydev.unity-mcp",
  join(repo, "third_party", "unity-mcp", "MCPForUnity"),
  "unity package: com.coplaydev.unity-mcp (bundled, MIT)"
);

// ---------------------------------------------------------------- 5. the Node programs

/**
 * True when the committed bundles were built from different sources than the ones in this checkout.
 *
 * A stale bundle is the one failure this layout introduces: .mcp.json points at bundled/, so an edit nobody
 * bundled means the running server silently is not the code being read. Compared by content hash rather than
 * by timestamps, which say nothing useful in a fresh clone where every file shares one checkout mtime.
 */
function bundlesAreStale() {
  const stamp = join(repo, "bundled", "BUNDLE.json");
  if (!existsSync(stamp)) return false;
  try {
    const recorded = JSON.parse(readFileSync(stamp, "utf8")).sourcesSha256;
    if (!recorded) return false;
    return hashSources(repo) !== recorded;
  } catch {
    // An older stamp, or an unreadable source tree: not a reason to hold up an install.
    return false;
  }
}

const bundledServer = join(repo, "bundled", "vrcw-mcp.mjs");
const bundledSupervisor = join(repo, "bundled", "vrcw-supervisor.mjs");
const distServer = join(repo, "packages", "vrcw-mcp", "dist", "index.js");
const distSupervisor = join(repo, "packages", "supervisor", "dist", "index.js");

let serverEntry = existsSync(bundledServer) ? bundledServer : existsSync(distServer) ? distServer : null;
let supervisorEntry = existsSync(bundledSupervisor) ? bundledSupervisor : existsSync(distSupervisor) ? distSupervisor : null;

if (serverEntry && supervisorEntry) {
  const which = serverEntry === bundledServer ? "committed bundles, no npm install needed" : "built from packages/";
  const stale = serverEntry === bundledServer ? bundlesAreStale() : false;
  if (stale) {
    step("node programs", "warn", `${which}: ${serverEntry}, but it was built from different sources`,
      "run: npm run build && npm run bundle — otherwise the server you start is not the source you are reading");
  } else {
    step("node programs", "ok", `${which}: ${serverEntry}`);
  }
} else if (options.skipBuild) {
  step("node programs", "fail", "no committed bundles and no build output, and --skip-build was given",
    "run: npm install && npm run build && npm run bundle");
} else if (have("npm")) {
  const built = run("npm", ["run", "build"], { timeout: 900_000 });
  serverEntry = existsSync(distServer) ? distServer : null;
  supervisorEntry = existsSync(distSupervisor) ? distSupervisor : null;
  if (built.ok && serverEntry && supervisorEntry) {
    step("node programs", "ok", "built from source");
  } else {
    step("node programs", "fail", `npm run build failed: ${built.out.slice(-600)}`,
      "the MCP server cannot start without it");
  }
} else {
  step("node programs", "fail", "no committed bundles, and npm is not on PATH",
    "install Node, then: npm install && npm run build");
}

// ---------------------------------------------------------------- 6. the linter

{
  const exe = join(
    repo, "packages", "udon-analyzer", "bin", "Release", "net8.0",
    process.platform === "win32" ? "udon-analyzer.exe" : "udon-analyzer"
  );
  const csproj = join(repo, "packages", "udon-analyzer", "UdonAnalyzer.csproj");

  if (existsSync(exe)) {
    step("udon analyzer", "ok", exe);
  } else if (options.skipBuild) {
    step("udon analyzer", "skip", "not built; the MCP server builds it on the first udon_lint call");
  } else if (have("dotnet")) {
    const built = run("dotnet", ["build", "-c", "Release", csproj]);
    if (built.ok && existsSync(exe)) step("udon analyzer", "ok", `built: ${exe}`);
    else
      step("udon analyzer", "warn", `dotnet build failed: ${built.out.slice(-400)}`,
        "udon_lint retries the build on first use; everything else works without it");
  } else {
    step("udon analyzer", "warn", "no dotnet SDK, so the fast linter is unavailable",
      "install the .NET 8 SDK; udon_lint builds it automatically once dotnet is on PATH");
  }
}

// ---------------------------------------------------------------- 7. the bundled Python server

const serverDir = join(repo, "third_party", "unity-mcp", "Server");
const uvVersion = options.skipServerDeps ? null : have("uv");

if (options.skipServerDeps) {
  step("companion server deps", "skip", "--skip-server-deps");
} else if (!existsSync(serverDir)) {
  step("companion server deps", "warn", `${serverDir} is missing from this checkout`);
} else if (!uvVersion) {
  step("companion server deps", "warn", "uv is not on PATH, so the generic Unity MCP server cannot run",
    "install uv (https://docs.astral.sh/uv/); the editor bridge and our own server work without it");
} else {
  const synced = run("uv", ["sync", "--frozen", "--directory", serverDir], { timeout: 900_000 });
  if (synced.ok) step("companion server deps", "ok", `uv sync --frozen with ${uvVersion}`);
  else
    step("companion server deps", "warn", `uv sync failed: ${synced.out.slice(-500)}`,
      "the generic half will not start until this succeeds; the VRChat half is unaffected");
}

// ---------------------------------------------------------------- 8. MCP client configuration

if (!options.mcpConfig) {
  step("mcp config", "skip", "--no-mcp-config");
} else if (!serverEntry) {
  step("mcp config", "skip", "nothing to point at until the Node programs build");
} else {
  const file = resolve(options.mcpConfig);
  let config = { mcpServers: {} };
  if (existsSync(file)) {
    try {
      config = JSON.parse(readFileSync(file, "utf8"));
      config.mcpServers ??= {};
    } catch {
      step("mcp config", "warn", `${file} was not valid JSON; writing a fresh one`);
      config = { mcpServers: {} };
    }
  }

  config.mcpServers.vrcw = { command: "node", args: [serverEntry.replace(/\\/g, "/")] };

  const companionRunnable = Boolean(uvVersion ?? have("uv"));
  if (companionRunnable) {
    config.mcpServers["unity-generic"] = {
      command: "uv",
      args: ["run", "--directory", serverDir.replace(/\\/g, "/"), "--frozen", "mcp-for-unity"],
      // Passed explicitly: this server reports usage to a third party unless told not to. The editor-side
      // half is switched off separately, from inside the bridge, because it has its own opt-out.
      env: { DISABLE_TELEMETRY: "1", UNITY_MCP_DISABLE_TELEMETRY: "1", MCP_DISABLE_TELEMETRY: "1" },
    };
  }

  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(config, null, 2) + "\n", "utf8");
  step("mcp config", "ok",
    `${file}: vrcw${companionRunnable ? " + unity-generic (telemetry off)" : " (unity-generic omitted: no uv)"}`);
}

// ---------------------------------------------------------------- 9. a live editor, if there is one

{
  let bridge = null;
  try {
    bridge = await import(new URL("../packages/vrcw-mcp/dist/bridge.js", import.meta.url).href);
  } catch {
    bridge = null;
  }

  if (!bridge) {
    step("live editor", "skip", "bridge client not built, so a running editor cannot be configured from here");
  } else {
    let health = null;
    try {
      health = await bridge.rpc("bridge.health", {}, { project, timeoutMs: 20_000 });
    } catch {
      health = null;
    }

    if (!health) {
      step("live editor", "skip", "no editor is running for this project",
        "open it; the bridge and the companion both come up on their own");
    } else {
      const actions = [];
      try {
        await bridge.rpc("bridge.harden_editor", { apply: true }, { project, timeoutMs: 60_000 });
        actions.push("hardened editor preferences");
      } catch (err) {
        actions.push(`harden_editor failed: ${String(err.message ?? err).slice(0, 160)}`);
      }
      try {
        const companion = await bridge.rpc("companion.setup", {}, { project, timeoutMs: 300_000 });
        actions.push(
          `companion ${companion.ready ? "ready" : "not ready yet"}` +
            (companion.actionsTaken?.length ? ` (${companion.actionsTaken.join("; ")})` : "")
        );
      } catch (err) {
        actions.push(`companion.setup failed: ${String(err.message ?? err).slice(0, 160)}`);
      }
      step("live editor", "ok",
        `pid ${health.pid} on port ${health.port}, ${health.methodCount} methods; ${actions.join("; ")}`);
    }
  }
}

finish();
