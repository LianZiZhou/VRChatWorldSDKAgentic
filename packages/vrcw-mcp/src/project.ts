/**
 * Checkpointing for the Unity project.
 *
 * Unity scenes are enormous generated YAML documents. An agent cannot sensibly merge one, cannot reliably
 * undo a bad scene edit, and Unity's own undo stack does not survive a domain reload — so the only workable
 * safety net is a commit before each risky step and a hard rollback after a bad one.
 *
 * Rollback is treated as genuinely destructive. It always commits the current state first, so "roll back"
 * never means "lose work", and it refuses to run without an explicit confirmation.
 */
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

export interface GitResult {
  ok: boolean;
  stdout: string;
  stderr: string;
  code: number | null;
}

/** Entries a Unity project must not commit: generated caches, and our own bridge state. */
const REQUIRED_IGNORES = [
  "Library/",
  "Temp/",
  "Obj/",
  "Build/",
  "Builds/",
  "Logs/",
  "UserSettings/",
  ".agentic/",
];

function git(projectPath: string, args: string[]): GitResult {
  const result = spawnSync("git", args, { cwd: projectPath, encoding: "utf8", timeout: 120_000 });
  return {
    ok: result.status === 0,
    stdout: (result.stdout ?? "").trim(),
    stderr: (result.stderr ?? "").trim(),
    code: result.status,
  };
}

function assertProject(projectPath: string): void {
  if (!projectPath) throw new Error("project path is required");
  if (!existsSync(join(projectPath, "ProjectSettings", "ProjectVersion.txt")))
    throw new Error(`Not a Unity project (no ProjectSettings/ProjectVersion.txt): ${projectPath}`);
}

function isRepo(projectPath: string): boolean {
  return git(projectPath, ["rev-parse", "--is-inside-work-tree"]).stdout === "true";
}

export function status(projectPath: string): Record<string, unknown> {
  assertProject(projectPath);

  if (!isRepo(projectPath)) {
    return {
      isRepo: false,
      hint:
        "This project is not under version control, so there is no way to undo a bad scene edit. " +
        "Call project_checkpoint with init:true to start tracking it.",
    };
  }

  const porcelain = git(projectPath, ["status", "--porcelain"]).stdout;
  const changed = porcelain ? porcelain.split("\n").map((l) => l.trim()) : [];
  const head = git(projectPath, ["rev-parse", "--short", "HEAD"]);
  const branch = git(projectPath, ["rev-parse", "--abbrev-ref", "HEAD"]);
  const lfs = git(projectPath, ["lfs", "version"]);

  const missingIgnores = missingIgnoreEntries(projectPath);

  return {
    isRepo: true,
    branch: branch.ok ? branch.stdout : null,
    head: head.ok ? head.stdout : null,
    hasCommits: head.ok,
    dirtyFileCount: changed.length,
    dirtyFiles: changed.slice(0, 50),
    gitLfsAvailable: lfs.ok,
    missingIgnoreEntries: missingIgnores,
    notes: [
      missingIgnores.length > 0
        ? `.gitignore is missing ${missingIgnores.join(", ")} — committing Unity's Library/ would add gigabytes of generated cache.`
        : null,
      lfs.ok
        ? null
        : "git-lfs is not installed. Textures, audio and models in a world project are large binaries that bloat history without it.",
    ].filter(Boolean),
  };
}

/**
 * Normalises a .gitignore line so the standard Unity template is recognised.
 *
 * Unity's official template writes `/[Ll]ibrary/`, not `Library/`. Comparing literally would report every
 * correctly-configured project as misconfigured and then append duplicate rules — so leading slashes,
 * case-insensitive character classes and trailing slashes are all folded away first.
 */
function normalizeIgnoreLine(line: string): string {
  return line
    .trim()
    .replace(/^\/+/, "")
    .replace(/\/+$/, "")
    .replace(/\[([A-Za-z])[A-Za-z]\]/g, "$1")
    .toLowerCase();
}

function missingIgnoreEntries(projectPath: string): string[] {
  const path = join(projectPath, ".gitignore");
  let contents = "";
  try {
    contents = existsSync(path) ? readFileSync(path, "utf8") : "";
  } catch {
    return REQUIRED_IGNORES;
  }
  const present = new Set(
    contents.split(/\r?\n/).filter((l) => l.trim() && !l.trim().startsWith("#")).map(normalizeIgnoreLine)
  );
  return REQUIRED_IGNORES.filter((entry) => !present.has(normalizeIgnoreLine(entry)));
}

function ensureIgnore(projectPath: string): string[] {
  const missing = missingIgnoreEntries(projectPath);
  if (missing.length === 0) return [];

  const path = join(projectPath, ".gitignore");
  const existing = existsSync(path) ? readFileSync(path, "utf8") : "";
  const addition =
    (existing && !existing.endsWith("\n") ? "\n" : "") +
    "\n# Added by vrcw-mcp: Unity caches and agentic bridge state must not be committed.\n" +
    missing.join("\n") +
    "\n";
  writeFileSync(path, existing + addition);
  return missing;
}

export function checkpoint(projectPath: string, message: string, init: boolean): Record<string, unknown> {
  assertProject(projectPath);

  const created: string[] = [];
  if (!isRepo(projectPath)) {
    if (!init)
      throw new Error(
        "This project is not a git repository, so a checkpoint cannot be made. Pass init:true to initialise one."
      );
    const initResult = git(projectPath, ["init"]);
    if (!initResult.ok) throw new Error(`git init failed: ${initResult.stderr}`);
    created.push("repository");
  }

  const addedIgnores = ensureIgnore(projectPath);
  if (addedIgnores.length > 0) created.push(`.gitignore entries: ${addedIgnores.join(", ")}`);

  const add = git(projectPath, ["add", "-A"]);
  if (!add.ok) throw new Error(`git add failed: ${add.stderr}`);

  const staged = git(projectPath, ["diff", "--cached", "--name-only"]).stdout;
  if (!staged) {
    const head = git(projectPath, ["rev-parse", "--short", "HEAD"]);
    return {
      committed: false,
      reason: "Nothing changed since the last checkpoint.",
      head: head.ok ? head.stdout : null,
      created,
    };
  }

  const commit = git(projectPath, [
    "-c", "user.name=vrcw-mcp",
    "-c", "user.email=vrcw-mcp@localhost",
    "commit", "-q", "-m", message,
  ]);
  if (!commit.ok) throw new Error(`git commit failed: ${commit.stderr || commit.stdout}`);

  const head = git(projectPath, ["rev-parse", "--short", "HEAD"]);
  return {
    committed: true,
    head: head.stdout,
    message,
    fileCount: staged.split("\n").length,
    created,
  };
}

export function checkpoints(projectPath: string, limit: number): Record<string, unknown> {
  assertProject(projectPath);
  if (!isRepo(projectPath)) return { isRepo: false, checkpoints: [] };

  // Space-separated on purpose: a short sha and an ISO-strict date both contain no spaces, so the first two
  // tokens are unambiguous and the rest is the subject. That avoids depending on a delimiter character
  // surviving every tool that touches this file.
  const log = git(projectPath, ["log", `-n${limit}`, "--date=iso-strict", "--pretty=format:%h %ad %s"]);
  if (!log.ok) return { isRepo: true, hasCommits: false, checkpoints: [] };

  const entries = log.stdout
    .split("\n")
    .filter(Boolean)
    .map((line) => {
      const match = /^(\S+)\s+(\S+)\s*(.*)$/.exec(line);
      return match
        ? { sha: match[1], date: match[2], message: match[3] }
        : { sha: null, date: null, message: line };
    });
  return { isRepo: true, hasCommits: true, checkpoints: entries };
}

/**
 * Hard-resets the project to a checkpoint.
 *
 * Always commits the current state first. That turns an irreversible operation into a reversible one: the
 * discarded work is still in history, so a rollback made by mistake can itself be rolled back.
 */
export function rollback(projectPath: string, target: string, confirm: boolean): Record<string, unknown> {
  assertProject(projectPath);
  if (!isRepo(projectPath)) throw new Error("This project is not a git repository, so there is nothing to roll back to.");

  if (!confirm)
    throw new Error(
      `Refusing to roll back without confirm:true. This discards every change since ${target} from the working tree.`
    );

  const resolved = git(projectPath, ["rev-parse", "--short", target]);
  if (!resolved.ok) throw new Error(`No such checkpoint "${target}": ${resolved.stderr}`);

  // Commit whatever is uncommitted, and also record where the branch was, so both the working tree and
  // the discarded commits can be recovered.
  const previousHead = git(projectPath, ["rev-parse", "HEAD"]).stdout;
  let safety: Record<string, unknown> | null = null;
  const porcelain = git(projectPath, ["status", "--porcelain"]).stdout;
  if (porcelain) {
    safety = checkpoint(projectPath, `safety checkpoint before rollback to ${resolved.stdout}`, false);
  }

  // Anchor the pre-rollback state under a real ref.
  //
  // reset --hard moves the branch pointer, which leaves those commits unreachable from any ref: they
  // survive only in the reflog and git gc is entitled to prune them. A safety net that can be garbage
  // collected is not a safety net, so this makes them reachable permanently.
  const anchorSha = git(projectPath, ["rev-parse", "HEAD"]).stdout || previousHead;
  const anchorRef = `refs/agentic/safety/${new Date().toISOString().replace(/[:.]/g, "-")}`;
  const anchored = anchorSha ? git(projectPath, ["update-ref", anchorRef, anchorSha]) : null;

  const reset = git(projectPath, ["reset", "--hard", resolved.stdout]);
  if (!reset.ok) throw new Error(`git reset failed: ${reset.stderr}`);

  const head = git(projectPath, ["rev-parse", "--short", "HEAD"]);
  const recoverSha = (safety?.head as string | undefined) ?? (anchorSha ? anchorSha.slice(0, 7) : null);

  return {
    rolledBackTo: head.stdout,
    requestedTarget: target,
    safetyCheckpoint: safety,
    safetyRef: anchored?.ok ? anchorRef : null,
    recoverWith: recoverSha ? `project_rollback to ${recoverSha} (also kept at ${anchorRef})` : null,
    notes: [
      "Unity still has the old scene in memory. Reopen the scene, or call unity_refresh_assets, before trusting " +
        "what the editor shows.",
      "Rewriting scene files under a running editor raises Unity's \"the open scene(s) have been modified " +
        "externally\" dialog, which blocks the editor. Run the supervisor with --auto-dismiss-dialogs true " +
        "during rollbacks, or answer it yourself.",
    ],
  };
}
