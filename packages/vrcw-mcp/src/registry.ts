import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";

export interface EditorInstance {
  projectPath: string;
  projectName: string;
  port: number;
  token: string;
  pid: number;
  unityVersion: string;
  bridgeVersion: string;
  startedUtc: string;
  registryFile: string;
  /** False when the recorded pid is no longer running — a crashed editor leaves its entry behind. */
  processAlive: boolean;
}

function registryDir(): string {
  const localAppData =
    process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local");
  return join(localAppData, "AgenticVrcw", "instances");
}

function isProcessAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    // Signal 0 performs the permission/existence check without delivering anything.
    process.kill(pid, 0);
    return true;
  } catch (err) {
    // EPERM means it exists but belongs to someone else, which still counts as alive.
    return (err as NodeJS.ErrnoException).code === "EPERM";
  }
}

/**
 * Editors are discovered through files the bridge writes to LOCALAPPDATA rather than through a fixed
 * port, because the bridge binds an ephemeral port and several projects may be open at once.
 */
export function listInstances(): EditorInstance[] {
  const dir = registryDir();
  if (!existsSync(dir)) return [];

  const instances: EditorInstance[] = [];
  for (const name of readdirSync(dir)) {
    if (!name.endsWith(".json")) continue;
    const file = join(dir, name);
    try {
      const raw = JSON.parse(readFileSync(file, "utf8"));
      instances.push({
        projectPath: String(raw.projectPath ?? ""),
        projectName: String(raw.projectName ?? ""),
        port: Number(raw.port ?? 0),
        token: String(raw.token ?? ""),
        pid: Number(raw.pid ?? 0),
        unityVersion: String(raw.unityVersion ?? ""),
        bridgeVersion: String(raw.bridgeVersion ?? ""),
        startedUtc: String(raw.startedUtc ?? ""),
        registryFile: file,
        processAlive: isProcessAlive(Number(raw.pid ?? 0)),
      });
    } catch {
      // A half-written registry file during editor startup is expected; skip it.
    }
  }
  instances.sort((a, b) => b.startedUtc.localeCompare(a.startedUtc));
  return instances;
}

function normalize(p: string): string {
  return p.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}

/**
 * Picks the editor to talk to. A hint may be a full project path or just a project name, which is
 * what an agent is likely to have on hand.
 */
export function resolveInstance(hint?: string): EditorInstance {
  const all = listInstances();
  const live = all.filter((i) => i.processAlive && i.port > 0 && i.token);

  if (live.length === 0) {
    const stale = all.length > 0 ? ` Found ${all.length} stale registry entry/entries (process no longer running).` : "";
    throw new Error(
      "No running Unity Editor with the agentic bridge was found." +
        stale +
        " Start the editor on a project that has the com.agentic.vrcw package installed, then retry."
    );
  }

  if (!hint) {
    if (live.length === 1) return live[0];
    const names = live.map((i) => `${i.projectName} (${i.projectPath})`).join(", ");
    throw new Error(
      `Several editors are running; pass project to choose one: ${names}`
    );
  }

  const needle = normalize(hint);
  const matches = live.filter(
    (i) => normalize(i.projectPath) === needle || i.projectName.toLowerCase() === hint.toLowerCase()
  );
  if (matches.length === 1) return matches[0];
  if (matches.length > 1) {
    throw new Error(`Hint "${hint}" matches several running editors; pass the full project path.`);
  }

  const available = live.map((i) => `${i.projectName} -> ${i.projectPath}`).join(", ");
  throw new Error(`No running editor matches "${hint}". Running: ${available}`);
}
