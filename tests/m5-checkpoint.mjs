#!/usr/bin/env node
/**
 * Checkpoint and rollback for the Unity project.
 *
 * These are the undo mechanism for scene edits. A Unity scene is a huge generated YAML document that cannot
 * be merged or hand-reverted, and Unity's undo stack does not survive a domain reload, so a commit before
 * each risky step is the only real safety net.
 *
 * The test asserts the safety properties as much as the happy path: that Unity's generated caches are never
 * staged, that rollback refuses without explicit confirmation, and that a rollback is itself reversible.
 */
import { existsSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import * as projectGit from "../packages/vrcw-mcp/dist/project.js";

const PROJECT = process.env.VRCW_PROJECT ?? "E:/Unity/WorldTest";
const PROBE = join(PROJECT, "Assets", "AgenticCheckpointProbe.txt");

let passed = 0;
let failed = 0;

function check(name, fn) {
  try {
    const detail = fn();
    passed++;
    console.log(`PASS  ${name}`);
    if (detail) console.log(`      ${detail}`);
  } catch (err) {
    failed++;
    console.log(`FAIL  ${name}`);
    console.log(`      ${String(err.message ?? err).split("\n").slice(0, 5).join("\n      ")}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

check("status describes an untracked or tracked project honestly", () => {
  const s = projectGit.status(PROJECT);
  if (!s.isRepo) {
    assert(s.hint && /not under version control/.test(s.hint), `unhelpful hint: ${s.hint}`);
    return "not a repo yet, and says why that matters";
  }
  return `repo at ${s.head ?? "(no commits)"}, ${s.dirtyFileCount} dirty, lfs=${s.gitLfsAvailable}`;
});

check("recognises the standard Unity .gitignore instead of duplicating it", () => {
  const s = projectGit.status(PROJECT);
  const ignorePath = join(PROJECT, ".gitignore");
  if (!existsSync(ignorePath)) return "project has no .gitignore; nothing to recognise";

  const text = readFileSync(ignorePath, "utf8");
  // Unity's official template writes /[Ll]ibrary/ rather than Library/.
  assert(/\[Ll\]ibrary/.test(text), "this project is not using the standard Unity template; test assumption is wrong");
  const missing = s.missingIgnoreEntries ?? [];
  assert(!missing.includes("Library/"), `Library/ reported missing despite /[Ll]ibrary/ being present: ${JSON.stringify(missing)}`);
  return `missing entries: ${JSON.stringify(missing)} (Library correctly recognised)`;
});

check("checkpoint refuses to initialise a repo unless asked", () => {
  const s = projectGit.status(PROJECT);
  if (s.isRepo) return "already a repo; refusal path not applicable";
  try {
    projectGit.checkpoint(PROJECT, "should not happen", false);
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/not a git repository/.test(err.message), `unhelpful: ${err.message}`);
    return "refused without init:true";
  }
});

check("checkpoint commits without staging Unity's generated caches", () => {
  const result = projectGit.checkpoint(PROJECT, "agentic: baseline checkpoint", true);
  assert(result.committed || result.reason, `no outcome reported: ${JSON.stringify(result)}`);

  // The real risk: Library/ is gigabytes of generated cache and must never enter history.
  const after = projectGit.status(PROJECT);
  assert(after.isRepo === true, "repo was not created");
  assert((after.missingIgnoreEntries ?? []).length === 0,
    `ignore rules still missing: ${JSON.stringify(after.missingIgnoreEntries)}`);

  const tracked = projectGit.checkpoints(PROJECT, 1);
  assert(tracked.hasCommits, "no commit exists after checkpointing");
  return result.committed
    ? `committed ${result.fileCount} files at ${result.head}`
    : `nothing to commit (${result.reason}), head ${result.head}`;
});

check("Library/ is genuinely not tracked", () => {
  // Asserted against git itself rather than inferred from the ignore file.
  const result = spawnSync("git", ["ls-files", "Library"], { cwd: PROJECT, encoding: "utf8" });
  const tracked = (result.stdout ?? "").trim();
  assert(tracked === "", `Library/ files are tracked: ${tracked.split("\n").slice(0, 3).join(", ")}`);
  return "no Library/ files in the index";
});

let baseline = null;

check("a change produces a new checkpoint", () => {
  writeFileSync(PROBE, `checkpoint probe written at ${new Date().toISOString()}\n`);
  const result = projectGit.checkpoint(PROJECT, "agentic: add checkpoint probe", false);
  assert(result.committed === true, `probe was not committed: ${JSON.stringify(result)}`);

  const log = projectGit.checkpoints(PROJECT, 5);
  assert(log.checkpoints.length >= 1, "no checkpoints listed");
  assert(log.checkpoints[0].sha, `checkpoint has no sha: ${JSON.stringify(log.checkpoints[0])}`);
  assert(/add checkpoint probe/.test(log.checkpoints[0].message), `unexpected subject: ${log.checkpoints[0].message}`);
  baseline = log.checkpoints.length > 1 ? log.checkpoints[1].sha : null;
  return `head ${result.head}: ${log.checkpoints[0].message} (parent ${baseline ?? "none"})`;
});

check("rollback refuses without confirmation", () => {
  try {
    projectGit.rollback(PROJECT, "HEAD~1", false);
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/confirm:true/.test(err.message), `unhelpful: ${err.message}`);
    return "refused without confirm:true";
  }
});

check("rollback reverts the change and stays recoverable", () => {
  assert(existsSync(PROBE), "probe file is missing before the rollback");

  // Dirty the tree so the safety-checkpoint path is exercised, not just the clean case.
  writeFileSync(PROBE, "uncommitted edit that must survive in history\n");

  const result = projectGit.rollback(PROJECT, "HEAD~1", true);
  assert(result.rolledBackTo, `no resulting head: ${JSON.stringify(result)}`);
  assert(result.safetyCheckpoint && result.safetyCheckpoint.committed,
    `uncommitted work was not preserved: ${JSON.stringify(result.safetyCheckpoint)}`);
  assert(result.recoverWith, "no recovery instruction given");
  assert(!existsSync(PROBE), "the probe file survived a rollback that should have removed it");
  assert(Array.isArray(result.notes) && result.notes.some((n) => /modified externally/.test(n)),
    "rollback did not warn about the editor's external-change dialog");
  return `rolled back to ${result.rolledBackTo}; recover with ${result.recoverWith}`;
});

check("the discarded work is reachable from a ref, not just the reflog", () => {
  // reset --hard leaves the old commits unreachable from any ref, where git gc may prune them. The
  // rollback anchors them under refs/agentic/safety/... so recovery does not depend on gc timing.
  const refs = spawnSync("git", ["for-each-ref", "--format=%(refname)", "refs/agentic"], { cwd: PROJECT, encoding: "utf8" });
  const anchors = (refs.stdout ?? "").trim().split(/\r?\n/).filter(Boolean);
  assert(anchors.length >= 1, "no safety ref was created, so the discarded work can be garbage collected");

  const log = spawnSync("git", ["log", "--oneline", "-n3", anchors[anchors.length - 1]], { cwd: PROJECT, encoding: "utf8" });
  assert(/safety checkpoint before rollback|add checkpoint probe/.test(log.stdout ?? ""),
    `safety ref does not contain the discarded work: ${(log.stdout ?? "").slice(0, 200)}`);
  return `${anchors.length} safety ref(s); newest contains: ${(log.stdout ?? "").split(/\r?\n/)[0]}`;
});

check("rollback rejects an unknown target", () => {
  try {
    projectGit.rollback(PROJECT, "definitely-not-a-sha", true);
    throw new Error("expected a refusal");
  } catch (err) {
    assert(/No such checkpoint/.test(err.message), `unhelpful: ${err.message}`);
    return "named the bad target";
  }
});

// Leave the probe gone; it was only ever a fixture.
try { rmSync(PROBE, { force: true }); } catch { /* already gone */ }

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
