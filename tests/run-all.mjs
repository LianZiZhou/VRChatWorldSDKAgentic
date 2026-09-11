#!/usr/bin/env node
/**
 * Runs every acceptance suite in order and prints one summary.
 *
 * Order is deliberate: the cheap, Unity-free suites first, so a broken checkout fails in seconds rather than
 * after a build; then the editor suites; then the end-to-end world build, which is the slowest and the one
 * that subsumes the others. A suite that fails does not stop the run — the point of a full pass is to learn
 * everything that is wrong, not the first thing.
 *
 *   node tests/run-all.mjs              every suite
 *   node tests/run-all.mjs --quick      skip the slow ones (builds, ClientSim, the end-to-end run)
 *   node tests/run-all.mjs --only m1    just the suites whose name contains "m1"
 */
import { spawnSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

/** slow: needs a build, a play-mode session, or a real dialog. */
const SUITES = [
  { file: "m5-lint-whitelist.mjs", covers: "out-of-Unity linter, Udon exposure whitelist" },
  { file: "m0-smoke.mjs", covers: "bridge transport, eval, reflection, jobs" },
  { file: "m1-udon.mjs", covers: "UdonSharp authoring loop" },
  { file: "m2-world.mjs", covers: "project setup, scaffolding, validation, audit, renders" },
  { file: "m4-budget-assets.mjs", covers: "dual-platform budget, provenance, procedural geometry" },
  { file: "m5-checkpoint.mjs", covers: "checkpoint / rollback safety" },
  { file: "mcp-handshake.mjs", covers: "MCP stdio protocol and tool surface" },
  { file: "companion-mcp.mjs", covers: "bundled generic Unity MCP layer" },
  { file: "m4-open-assets.mjs", covers: "open-licence fetching (needs network)" },
  { file: "m3-verify.mjs", covers: "ClientSim behaviour, local builds, real-client preflight", slow: true },
  { file: "dialog-recovery.mjs", covers: "raises a real modal dialog and recovers", slow: true },
  { file: "e2e-world.mjs", covers: "scaffold -> script -> verify -> build a real world", slow: true },
];

const argv = process.argv.slice(2);
const quick = argv.includes("--quick");
const onlyIndex = argv.indexOf("--only");
const only = onlyIndex >= 0 ? argv[onlyIndex + 1] : null;

const selected = SUITES.filter((suite) => {
  if (only) return suite.file.includes(only);
  return !(quick && suite.slow);
});

const results = [];
const startedAll = Date.now();

for (const suite of selected) {
  process.stdout.write(`\n${"=".repeat(78)}\n${suite.file} — ${suite.covers}\n${"=".repeat(78)}\n`);
  const started = Date.now();
  const run = spawnSync(process.execPath, [join(here, suite.file)], { stdio: "inherit", timeout: 30 * 60_000 });
  const seconds = Math.round((Date.now() - started) / 1000);

  // Suites print "N/M checks passed" themselves; the exit code is what this run records.
  results.push({
    file: suite.file,
    ok: !run.error && run.status === 0,
    seconds,
    detail: run.error ? String(run.error.message) : `exit ${run.status}`,
  });
}

const failed = results.filter((r) => !r.ok);
const total = Math.round((Date.now() - startedAll) / 1000);

process.stdout.write(`\n${"=".repeat(78)}\nSummary (${total}s)\n${"=".repeat(78)}\n`);
for (const result of results) {
  process.stdout.write(`${result.ok ? "PASS" : "FAIL"}  ${result.file.padEnd(26)} ${String(result.seconds).padStart(4)}s` +
    `${result.ok ? "" : `  ${result.detail}`}\n`);
}
process.stdout.write(`\n${results.length - failed.length}/${results.length} suites passed`);
process.stdout.write(`${quick ? " (--quick: slow suites skipped)" : ""}\n`);

process.exit(failed.length ? 1 : 0);
