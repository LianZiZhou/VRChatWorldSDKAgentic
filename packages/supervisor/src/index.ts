#!/usr/bin/env node
/**
 * Unity Editor supervisor.
 *
 * The VRChat world build API cannot be driven by `-batchmode -executeMethod`: TryGetBuilder requires a
 * live SDK control-panel EditorWindow, and batch mode does not pump EditorApplication.update, which the
 * SDK's UniTask-based build needs in order to progress. So an autonomous run depends on a long-lived
 * GUI editor staying up, and something has to own that process. This is that something.
 *
 * Restart policy is deliberately asymmetric:
 *   - process died            -> restart automatically. Nothing is lost that was not already lost.
 *   - main thread not pumping -> report, do not kill unless explicitly allowed. A lightmap bake, a
 *     large import, or a synchronous UdonSharp compile all legitimately freeze the pump for minutes,
 *     and killing the editor then would throw away real work.
 */
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { homedir } from "node:os";
import {
  discoverEditors,
  pickEditorForProject,
  readEditorInstall,
  readProjectVersion,
  verifyEditorForProject,
} from "./unity.js";

interface Config {
  unityExe: string;
  projectPath: string;
  logFile: string;
  /** Seconds without a pump tick before the editor is called stalled. */
  stallWarnSeconds: number;
  /** Seconds without a pump tick before a restart is considered (needs allowWedgeRestart). */
  stallRestartSeconds: number;
  allowWedgeRestart: boolean;
  /** Opens the project with a non-matching editor. Never appropriate for a VRChat project. */
  allowVersionMismatch: boolean;
  /** Grace period after a launch during which a not-yet-registered editor counts as starting. */
  startupGraceSeconds: number;
  pollSeconds: number;
}

interface BridgeInfo {
  projectPath: string;
  projectName: string;
  port: number;
  token: string;
  pid: number;
  unityVersion: string;
}

const DEFAULTS = {
  stallWarnSeconds: 60,
  stallRestartSeconds: 900,
  allowWedgeRestart: false,
  pollSeconds: 10,
  startupGraceSeconds: 300,
};

// ------------------------------------------------------------------ helpers

function registryDir(): string {
  const localAppData = process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local");
  return join(localAppData, "AgenticVrcw", "instances");
}

function findBridgeInfo(projectPath: string): BridgeInfo | null {
  const want = projectPath.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
  const dir = registryDir();
  if (!existsSync(dir)) return null;
  for (const name of readdirSync(dir)) {
    if (!name.endsWith(".json")) continue;
    try {
      const raw = JSON.parse(readFileSync(join(dir, name), "utf8")) as BridgeInfo;
      if (String(raw.projectPath ?? "").replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase() === want) return raw;
    } catch {
      /* partially written during editor startup */
    }
  }
  return null;
}

function isAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return (err as NodeJS.ErrnoException).code === "EPERM";
  }
}

async function probePing(port: number): Promise<Record<string, unknown> | null> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  try {
    const res = await fetch(`http://127.0.0.1:${port}/ping`, { signal: controller.signal });
    const body = (await res.json()) as { ok?: boolean; result?: Record<string, unknown> };
    return body?.ok ? body.result ?? null : null;
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

function statePath(projectPath: string): string {
  return join(projectPath, ".agentic", "supervisor.json");
}

function writeState(cfg: Config, state: Record<string, unknown>): void {
  try {
    mkdirSync(join(cfg.projectPath, ".agentic"), { recursive: true });
    writeFileSync(statePath(cfg.projectPath), JSON.stringify({ ...state, updatedUtc: new Date().toISOString() }, null, 2));
  } catch {
    /* state reporting must never take the supervisor down */
  }
}

function log(message: string): void {
  process.stdout.write(`[${new Date().toISOString()}] ${message}\n`);
}

// ------------------------------------------------------------------ actions

/**
 * Resolves which editor to use, verifies it against the project, and starts it detached.
 *
 * The verification is the important part. A VRChat project pinned to 2022.3.22f1 must be opened by the
 * international build of exactly that revision; a Unity China editor of the nominally same version will
 * offer to migrate the project, and an autonomous run has nobody to decline that prompt.
 */
function launch(cfg: Config): number {
  const install = cfg.allowVersionMismatch
    ? readEditorInstall(cfg.unityExe)
    : pickEditorForProject(cfg.projectPath, cfg.unityExe || undefined);

  if (cfg.allowVersionMismatch) {
    const verdict = verifyEditorForProject(install, readProjectVersion(cfg.projectPath));
    if (!verdict.ok) log(`WARNING proceeding despite version mismatch: ${verdict.reasons.join("; ")}`);
  }

  mkdirSync(dirname(cfg.logFile), { recursive: true });

  const child = spawn(install.exe, ["-projectPath", cfg.projectPath, "-logFile", cfg.logFile], {
    detached: true,
    stdio: "ignore",
  });
  child.unref();
  log(`launched Unity ${install.productVersion} pid=${child.pid} project=${cfg.projectPath}`);
  return child.pid ?? -1;
}

/** Reports every editor on the machine and whether it may open this project. */
function editors(cfg: Config): Record<string, unknown> {
  const project = readProjectVersion(cfg.projectPath);
  return {
    project,
    installs: discoverEditors().map((install) => {
      const verdict = verifyEditorForProject(install, project);
      return {
        exe: install.exe,
        productVersion: install.productVersion,
        version: install.version,
        revision: install.revision,
        isUnityChina: install.isUnityChina,
        hasLicensingClient: install.hasLicensingClient,
        usable: verdict.ok,
        reasons: verdict.reasons,
        warnings: verdict.warnings,
      };
    }),
  };
}

async function status(cfg: Config): Promise<Record<string, unknown>> {
  const info = findBridgeInfo(cfg.projectPath);
  if (!info) {
    return { bridgeRegistered: false, processAlive: false, verdict: "not-running" };
  }
  const processAlive = isAlive(info.pid);
  const ping = processAlive ? await probePing(info.port) : null;

  let verdict: string;
  let secondsSincePump: number | null = null;
  if (!processAlive) verdict = "dead";
  else if (!ping) verdict = "unreachable";
  else {
    const lastPump = String(ping.lastPumpUtc ?? "");
    secondsSincePump = lastPump ? (Date.now() - Date.parse(lastPump)) / 1000 : null;
    if (secondsSincePump !== null && secondsSincePump > cfg.stallWarnSeconds) verdict = "stalled";
    else verdict = "healthy";
  }

  return {
    bridgeRegistered: true,
    projectName: info.projectName,
    unityVersion: info.unityVersion,
    pid: info.pid,
    port: info.port,
    processAlive,
    reachable: Boolean(ping),
    secondsSincePump,
    queueDepth: ping?.queueDepth ?? null,
    pumpTicks: ping?.pumpTicks ?? null,
    verdict,
  };
}

function kill(pid: number): void {
  if (process.platform === "win32") spawnSync("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore" });
  else process.kill(pid, "SIGKILL");
}

async function watch(cfg: Config): Promise<void> {
  log(
    `watching ${cfg.projectPath} (poll ${cfg.pollSeconds}s, stall warn ${cfg.stallWarnSeconds}s, ` +
      `wedge restart ${cfg.allowWedgeRestart ? `after ${cfg.stallRestartSeconds}s` : "disabled"})`
  );

  let restarts = 0;
  let lastVerdict = "";
  // Tracked so a starting editor is not mistaken for a dead one. A fresh editor takes tens of seconds
  // to rebuild its domain and write its registry entry, and until it does the registry still holds the
  // *previous* editor's dead pid. Without this, the watcher reads "dead", launches again, and ends up
  // with two editors fighting over the same project lock.
  let spawnedPid = -1;
  let spawnedAtMs = 0;

  for (;;) {
    const snapshot = await status(cfg);
    let verdict = String(snapshot.verdict);

    const spawnStarting =
      spawnedPid > 0 &&
      isAlive(spawnedPid) &&
      Date.now() - spawnedAtMs < cfg.startupGraceSeconds * 1000;

    if ((verdict === "not-running" || verdict === "dead") && spawnStarting) {
      verdict = "starting";
    }

    if (verdict !== lastVerdict) {
      log(`verdict=${verdict} ${JSON.stringify(snapshot)}`);
      lastVerdict = verdict;
    }
    writeState(cfg, { ...snapshot, verdict, restarts, spawnedPid, allowWedgeRestart: cfg.allowWedgeRestart });

    if (verdict === "not-running" || verdict === "dead") {
      log("editor is not running; relaunching");
      try {
        spawnedPid = launch(cfg);
        spawnedAtMs = Date.now();
        restarts += 1;
      } catch (err) {
        log(`relaunch failed: ${(err as Error).message}`);
        writeState(cfg, { ...snapshot, restarts, launchError: (err as Error).message });
        return; // a misconfigured install will not fix itself by retrying
      }
      await sleep(15_000); // let it get going before the next probe
    } else if (verdict === "stalled") {
      const stalledFor = Number(snapshot.secondsSincePump ?? 0);
      if (cfg.allowWedgeRestart && stalledFor > cfg.stallRestartSeconds) {
        log(`main thread stalled ${Math.round(stalledFor)}s; killing pid=${snapshot.pid} and relaunching`);
        kill(Number(snapshot.pid));
        await sleep(5_000);
        launch(cfg);
        restarts += 1;
        await sleep(30_000);
      } else {
        // Not an error by itself: bakes, imports and synchronous U# compiles all look like this.
        log(`main thread stalled ${Math.round(stalledFor)}s (not restarting)`);
      }
    }

    await sleep(cfg.pollSeconds * 1000);
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// ------------------------------------------------------------------ cli

function parseArgs(argv: string[]): { command: string; cfg: Config } {
  const command = argv[0] ?? "help";
  const flags = new Map<string, string>();
  for (let i = 1; i < argv.length; i++) {
    const arg = argv[i];
    if (!arg.startsWith("--")) continue;
    const eq = arg.indexOf("=");
    if (eq > 0) flags.set(arg.slice(2, eq), arg.slice(eq + 1));
    else flags.set(arg.slice(2), argv[++i] ?? "true");
  }

  const projectPath = flags.get("project") ?? process.env.VRCW_PROJECT ?? "";
  const unityExe = flags.get("unity") ?? process.env.VRCW_UNITY ?? "";

  return {
    command,
    cfg: {
      unityExe,
      projectPath,
      logFile: flags.get("log") ?? join(projectPath, ".agentic", "unity-editor.log"),
      stallWarnSeconds: Number(flags.get("stall-warn") ?? DEFAULTS.stallWarnSeconds),
      stallRestartSeconds: Number(flags.get("stall-restart") ?? DEFAULTS.stallRestartSeconds),
      allowWedgeRestart: (flags.get("allow-wedge-restart") ?? "") === "true",
      allowVersionMismatch: (flags.get("allow-version-mismatch") ?? "") === "true",
      startupGraceSeconds: Number(flags.get("startup-grace") ?? DEFAULTS.startupGraceSeconds),
      pollSeconds: Number(flags.get("poll") ?? DEFAULTS.pollSeconds),
    },
  };
}

const USAGE = `vrcw-supervisor <command> --project <unityProjectPath> [--unity <Unity.exe>]

Commands
  start     Launch the editor if it is not already running.
  status    Print a one-shot health verdict as JSON.
  watch     Stay resident: relaunch on death, report stalls.
  stop      Kill the editor for this project.
  editors   List every Unity install and whether it may open this project.

Options
  --project PATH               Unity project root (or env VRCW_PROJECT)
  --unity PATH                 Unity.exe (or env VRCW_UNITY). Omit to auto-pick the install whose
                               version AND revision match the project.
  --poll N                     Seconds between health probes (default ${DEFAULTS.pollSeconds})
  --startup-grace N            Seconds a freshly launched editor may take to register before it is
                               treated as dead (default ${DEFAULTS.startupGraceSeconds})
  --stall-warn N               Seconds without a pump tick before reporting a stall (default ${DEFAULTS.stallWarnSeconds})
  --stall-restart N            Seconds before a stalled editor may be killed (default ${DEFAULTS.stallRestartSeconds})
  --allow-wedge-restart true   Permit killing a stalled editor. Off by default: long bakes and imports
                               stall the pump legitimately, and a kill would discard real work.
  --allow-version-mismatch true
                               Open the project with a non-matching editor. Do not use on VRChat
                               projects: a Unity China ("...f1c1") editor is unsupported by VRChat and
                               will offer a project migration that rewrites ProjectVersion.txt.
`;

async function main(): Promise<void> {
  const { command, cfg } = parseArgs(process.argv.slice(2));

  if (command === "help" || command === "--help" || command === "-h") {
    process.stdout.write(USAGE);
    return;
  }
  if (!cfg.projectPath) {
    process.stderr.write("--project is required\n\n" + USAGE);
    process.exit(2);
  }

  switch (command) {
    case "start": {
      const snapshot = await status(cfg);
      if (snapshot.verdict === "healthy" || snapshot.verdict === "stalled") {
        log(`already running (pid=${snapshot.pid}, verdict=${snapshot.verdict})`);
        return;
      }
      launch(cfg);
      return;
    }
    case "status": {
      process.stdout.write(JSON.stringify(await status(cfg), null, 2) + "\n");
      return;
    }
    case "editors": {
      process.stdout.write(JSON.stringify(editors(cfg), null, 2) + "\n");
      return;
    }
    case "stop": {
      const info = findBridgeInfo(cfg.projectPath);
      if (!info || !isAlive(info.pid)) {
        log("nothing to stop");
        return;
      }
      kill(info.pid);
      log(`killed pid=${info.pid}`);
      return;
    }
    case "watch":
      await watch(cfg);
      return;
    default:
      process.stderr.write(`Unknown command "${command}"\n\n` + USAGE);
      process.exit(2);
  }
}

main().catch((err) => {
  process.stderr.write(`supervisor failed: ${(err as Error).stack ?? err}\n`);
  process.exit(1);
});
