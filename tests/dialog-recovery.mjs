#!/usr/bin/env node
/**
 * Proves the modal-dialog defence end to end.
 *
 * A modal dialog is the worst failure this toolchain has: it halts Unity's update loop, so the bridge's
 * main-thread pump stops, every call returns EDITOR_BUSY, and an unattended run stops dead with nobody to
 * click the button. This happened for real — Unity's "detected external changes, reload?" prompt blocked a
 * whole test run.
 *
 * So this test raises a real modal dialog on purpose and checks that:
 *   1. /ping keeps answering (the process is alive and the bridge is reachable)
 *   2. /rpc reports EDITOR_BUSY rather than hanging forever
 *   3. the supervisor can name the blocking window, and tells a dialog apart from a floating editor panel
 *   4. the allowlist covers the prompt Unity really raises, and does not cover an unrecognised one
 *   5. the supervisor can answer it and the editor recovers
 *
 * The synthetic dialog is deliberately titled so that it does *not* match the auto-dismiss allowlist. A
 * supervisor watching this project with --auto-dismiss-dialogs — which is how the README says to run it —
 * would otherwise answer it within a poll interval, and this suite would be racing the defence it exists to
 * observe. The allowlist is checked as data instead, against the title Unity actually uses.
 */
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { ping, rpc, BridgeError } from "../packages/vrcw-mcp/dist/bridge.js";
import { findDialogs, matchAllowlist, dismissDialog } from "../packages/supervisor/dist/dialogs.js";

let passed = 0;
let failed = 0;

function report(name, ok, detail) {
  if (ok) { passed++; console.log(`PASS  ${name}`); } else { failed++; console.log(`FAIL  ${name}`); }
  if (detail) console.log(`      ${detail}`);
}

function bridgePid() {
  const dir = join(process.env.LOCALAPPDATA, "AgenticVrcw", "instances");
  const file = join(dir, readdirSync(dir).find((f) => f.endsWith(".json")));
  return JSON.parse(readFileSync(file, "utf8")).pid;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Sanity: the editor must be healthy before we break it.
try {
  const before = await rpc("bridge.health", {}, { timeoutMs: 20_000 });
  report("editor healthy before the test", Number(before.secondsSincePump) < 10,
    `secondsSincePump=${Number(before.secondsSincePump).toFixed(2)}`);
} catch (err) {
  report("editor healthy before the test", false, err.message);
  console.log("\nAborting: the editor must be responsive to run this test.");
  process.exit(1);
}

const pid = bridgePid();

// Raise the dialog. This call runs on the main thread and therefore never returns until the dialog is
// answered, so it is deliberately not awaited.
const blocked = rpc(
  "unity.eval_csharp",
  {
    code: `EditorUtility.DisplayDialog("Agentic synthetic probe",
  "Synthetic dialog raised by tests/dialog-recovery.mjs to verify the supervisor can see and answer it.", "OK", "Cancel");
return "dialog answered";`,
  },
  { timeoutMs: 20_000, reconnectWindowMs: 20_000 }
).catch((err) => ({ error: err }));

await sleep(4000);

// 1. ping must still answer: it is served from the listener thread and never touches the main thread.
try {
  const p = await ping();
  report("ping still answers while the editor is blocked", p.alive === true,
    `pid=${p.pid} pumpTicks=${p.pumpTicks} (frozen)`);
} catch (err) {
  report("ping still answers while the editor is blocked", false, err.message);
}

// 2. An rpc that needs the main thread must fail as EDITOR_BUSY, not hang.
try {
  await rpc("unity.editor_state", {}, { timeoutMs: 6_000, reconnectWindowMs: 6_000 });
  report("main-thread rpc reports EDITOR_BUSY", false, "the call unexpectedly succeeded");
} catch (err) {
  const isBusy = err instanceof BridgeError && err.code === "EDITOR_BUSY";
  report("main-thread rpc reports EDITOR_BUSY", isBusy, `${err.code ?? "?"}: ${String(err.message).slice(0, 120)}`);
}

// 3. The supervisor must be able to say what is in the way.
const dialogs = findDialogs(pid);
const target = dialogs.find((d) => /Agentic synthetic probe/i.test(d.title));
report("supervisor names the blocking dialog", Boolean(target),
  dialogs.length ? dialogs.map((d) => `"${d.title}" (${d.className})`).join(", ") : "no dialogs found");

// 4. The allowlist must cover it, and dismissing must unblock the editor.
if (target) {
  // Checked as data rather than by raising an allowlisted dialog: a supervisor watching this project with
  // --auto-dismiss-dialogs would answer that one within a poll interval, and the test would then be racing
  // the defence it is trying to observe. The string is the one Unity actually raises.
  const real = matchAllowlist("The open scene(s) have been modified externally");
  report("the real external-modification prompt is on the auto-dismiss allowlist", real.allowed === true,
    real.note ?? "not allowlisted");

  const synthetic = matchAllowlist(target.title);
  report("an unrecognised dialog is not auto-dismissed", synthetic.allowed === false,
    `"${target.title}" -> ${synthetic.allowed ? "allowed, which it should not be" : "reported, not answered"}`);

  const outcome = dismissDialog(target);
  report("dismiss was attempted", outcome.attempted === true, JSON.stringify(outcome));

  let recovered = false;
  let detail = "";
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    try {
      const health = await rpc("bridge.health", {}, { timeoutMs: 8_000, reconnectWindowMs: 8_000 });
      if (Number(health.secondsSincePump) < 10) {
        recovered = true;
        detail = `pump running again, secondsSincePump=${Number(health.secondsSincePump).toFixed(2)}`;
        break;
      }
    } catch {
      /* still blocked */
    }
    await sleep(1500);
  }
  report("editor recovers after the dialog is answered", recovered, detail || "still blocked after 30s");
}

const result = await blocked;
if (result && result.error) {
  console.log(`      (the blocking eval reported: ${String(result.error.message).slice(0, 90)})`);
} else if (result) {
  console.log(`      (the blocking eval returned: ${JSON.stringify(result.value ?? null)})`);
}

if (failed > 0) {
  console.log("\nIf the editor is still blocked, answer the dialog in Unity manually.");
}
console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
