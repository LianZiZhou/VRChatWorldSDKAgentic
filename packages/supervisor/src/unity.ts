/**
 * Unity install discovery and version verification.
 *
 * This exists because of a trap that is easy to fall into and expensive to notice: a machine can have
 * several "2022.3.22f1" editors where only one is the build VRChat supports.
 *
 *   E:\UnityEditor\Unity 2022.3.22f1   -> ProductVersion 2022.3.22f1_887be4894c44   (international)
 *   C:\Program Files\Unity\Hub\Editor\2022.3.22f1
 *                                      -> ProductVersion 2022.3.22f1c1_c3cbd310e76e (Unity China)
 *
 * The `c1` suffix marks a Unity China build, which VRChat does not support. Worse, the folder name and
 * metadata.hub.json of the China install both just say "2022.3.22f1" — only the executable's PE
 * ProductVersion tells the truth, so that is what we read. Opening a VRChat project with the wrong one
 * prompts a project migration, and accepting it would silently rewrite ProjectVersion.txt.
 */
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { homedir } from "node:os";

export interface ProjectVersion {
  version: string;
  revision: string | null;
}

export interface EditorInstall {
  exe: string;
  /** Full PE ProductVersion, e.g. "2022.3.22f1_887be4894c44". */
  productVersion: string;
  /** Version without the revision, e.g. "2022.3.22f1" or "2022.3.22f1c1". */
  version: string;
  revision: string | null;
  /** True when the version carries a Unity China `cN` suffix. */
  isUnityChina: boolean;
  hasLicensingClient: boolean;
}

/** Parses ProjectSettings/ProjectVersion.txt, which records both version and source revision. */
export function readProjectVersion(projectPath: string): ProjectVersion {
  const file = join(projectPath, "ProjectSettings", "ProjectVersion.txt");
  if (!existsSync(file)) throw new Error(`Not a Unity project (no ProjectVersion.txt): ${projectPath}`);

  const text = readFileSync(file, "utf8");
  const version = /m_EditorVersion:\s*(\S+)/.exec(text)?.[1] ?? "";
  const revision = /m_EditorVersionWithRevision:\s*\S+\s*\(([0-9a-f]+)\)/.exec(text)?.[1] ?? null;
  if (!version) throw new Error(`Could not parse m_EditorVersion from ${file}`);
  return { version, revision };
}

/**
 * Reads an editor's real version from the executable. Folder names and metadata.hub.json both lie for
 * Unity China installs, so neither is consulted.
 */
export function readEditorInstall(exe: string): EditorInstall {
  if (!existsSync(exe)) throw new Error(`Unity executable not found: ${exe}`);

  const productVersion = readProductVersion(exe);
  const [version, revision] = splitProductVersion(productVersion);
  const editorRoot = dirname(dirname(exe)); // <root>/Editor/Unity.exe

  return {
    exe,
    productVersion,
    version,
    revision,
    isUnityChina: /[abfp]\d+c\d+$/.test(version),
    hasLicensingClient: existsSync(
      join(editorRoot, "Editor", "Data", "Resources", "Licensing", "Client", "Unity.Licensing.Client.exe")
    ),
  };
}

function readProductVersion(exe: string): string {
  if (process.platform !== "win32") return "";
  const result = spawnSync(
    "powershell",
    ["-NoProfile", "-NonInteractive", "-Command", `(Get-Item -LiteralPath '${exe.replace(/'/g, "''")}').VersionInfo.ProductVersion`],
    { encoding: "utf8" }
  );
  return (result.stdout ?? "").trim();
}

function splitProductVersion(productVersion: string): [string, string | null] {
  // Shaped like "2022.3.22f1_887be4894c44"; the China build is "2022.3.22f1c1_c3cbd310e76e".
  const underscore = productVersion.indexOf("_");
  if (underscore < 0) return [productVersion, null];
  return [productVersion.slice(0, underscore), productVersion.slice(underscore + 1)];
}

export interface VersionVerdict {
  ok: boolean;
  reasons: string[];
  warnings: string[];
}

/**
 * Decides whether an editor may open a project. Revision equality is the real test: two installs can
 * share a version string and still be different builds.
 */
export function verifyEditorForProject(install: EditorInstall, project: ProjectVersion): VersionVerdict {
  const reasons: string[] = [];
  const warnings: string[] = [];

  if (install.isUnityChina) {
    reasons.push(
      `${install.exe} is a Unity China build (${install.version}). VRChat does not support Unity China ` +
        `editors; opening a VRChat project with one offers a project migration that would rewrite ` +
        `ProjectVersion.txt.`
    );
  }

  if (install.version && project.version && install.version !== project.version) {
    reasons.push(`Editor version ${install.version} does not match the project's ${project.version}.`);
  }

  if (project.revision && install.revision && project.revision !== install.revision) {
    reasons.push(
      `Editor revision ${install.revision} does not match the project's ${project.revision}. ` +
        `Same version string, different build.`
    );
  }

  if (!install.hasLicensingClient) {
    reasons.push(
      `${install.exe} is an incomplete install: no Unity.Licensing.Client.exe. Unity exits with code ` +
        `199 before loading the project.`
    );
  }

  if (!project.revision) warnings.push("Project records no editor revision, so only the version string could be checked.");
  if (!install.productVersion) warnings.push("Could not read the editor's ProductVersion; version checking was limited.");

  return { ok: reasons.length === 0, reasons, warnings };
}

/**
 * Every editor this machine appears to have, including ones added to the Hub by hand — which is how the
 * only VRChat-supported editor on the development machine happened to be installed.
 */
export function discoverEditors(): EditorInstall[] {
  const candidates = new Set<string>();

  // Hub's own record of manually added editors.
  const appData = process.env.APPDATA ?? join(homedir(), "AppData", "Roaming");
  try {
    const raw = JSON.parse(readFileSync(join(appData, "UnityHub", "editors-v2.json"), "utf8"));
    for (const entry of raw?.data ?? []) {
      for (const location of entry?.location ?? []) candidates.add(String(location));
    }
  } catch {
    /* no hub record */
  }

  // Default and secondary install roots.
  const roots = new Set<string>(["C:\\Program Files\\Unity\\Hub\\Editor"]);
  try {
    const secondary = JSON.parse(readFileSync(join(appData, "UnityHub", "secondaryInstallPath.json"), "utf8"));
    if (typeof secondary === "string" && secondary.trim()) roots.add(secondary);
  } catch {
    /* no secondary path configured */
  }

  for (const root of roots) {
    if (!existsSync(root)) continue;
    for (const name of readdirSync(root)) {
      const exe = join(root, name, "Editor", "Unity.exe");
      if (existsSync(exe)) candidates.add(exe);
    }
  }

  const installs: EditorInstall[] = [];
  for (const exe of candidates) {
    try {
      installs.push(readEditorInstall(exe));
    } catch {
      /* unreadable candidate */
    }
  }
  return installs;
}

/** The one install that can legitimately open this project, or a thrown explanation of why none can. */
export function pickEditorForProject(projectPath: string, explicitExe?: string): EditorInstall {
  const project = readProjectVersion(projectPath);

  if (explicitExe) {
    const install = readEditorInstall(explicitExe);
    const verdict = verifyEditorForProject(install, project);
    if (!verdict.ok) {
      throw new Error(
        `Refusing to open ${projectPath} with ${explicitExe}:\n  - ` +
          verdict.reasons.join("\n  - ") +
          `\nPass --allow-version-mismatch to override (not recommended for VRChat projects).`
      );
    }
    return install;
  }

  const installs = discoverEditors();
  const usable = installs.filter((i) => verifyEditorForProject(i, project).ok);
  if (usable.length > 0) return usable[0];

  const detail = installs
    .map((i) => {
      const verdict = verifyEditorForProject(i, project);
      return `  ${i.exe}\n    version=${i.productVersion || "unknown"}${i.isUnityChina ? " (Unity China)" : ""}\n    rejected: ${verdict.reasons.join("; ")}`;
    })
    .join("\n");

  throw new Error(
    `No installed editor matches project ${project.version} (revision ${project.revision ?? "unknown"}).\n` +
      (detail || "  (no Unity installs found)") +
      `\nInstall the international ${project.version} build, or pass --unity with its path.`
  );
}
