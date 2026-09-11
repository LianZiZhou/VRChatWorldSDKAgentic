#!/usr/bin/env node
/**
 * Checks the MCP layer itself: a real stdio handshake, the tool list, and one tool call that reaches
 * the live editor. The bridge tests exercise the transport below this; this covers the protocol above it.
 */
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, "..", "packages", "vrcw-mcp", "dist", "index.js");

const child = spawn(process.execPath, [server], { stdio: ["pipe", "pipe", "pipe"] });

let buffer = "";
const pending = new Map();

child.stdout.on("data", (chunk) => {
  buffer += chunk.toString("utf8");
  // MCP over stdio is newline-delimited JSON.
  let newline;
  while ((newline = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    let message;
    try { message = JSON.parse(line); } catch { continue; }
    if (message.id !== undefined && pending.has(message.id)) {
      const { resolve } = pending.get(message.id);
      pending.delete(message.id);
      resolve(message);
    }
  }
});

const stderrChunks = [];
child.stderr.on("data", (c) => stderrChunks.push(c.toString("utf8")));

let nextId = 1;
function request(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        reject(new Error(`timed out waiting for ${method}. stderr: ${stderrChunks.join("")}`));
      }
    }, 180_000);
  });
}

function notify(method, params) {
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");
}

let passed = 0;
let failed = 0;
function report(name, ok, detail) {
  if (ok) { passed++; console.log(`PASS  ${name}`); } else { failed++; console.log(`FAIL  ${name}`); }
  if (detail) console.log(`      ${detail}`);
}

try {
  const init = await request("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "handshake-test", version: "0" },
  });
  report("initialize", Boolean(init.result?.serverInfo), `server=${JSON.stringify(init.result?.serverInfo)}`);
  notify("notifications/initialized", {});

  const list = await request("tools/list", {});
  const tools = list.result?.tools ?? [];
  const names = tools.map((t) => t.name).sort();

  const expected = [
    "unity_ping", "unity_health", "unity_editor_state", "unity_console", "unity_compile_status",
    "unity_eval_csharp", "unity_invoke_static", "unity_get_static", "unity_menu_item",
    "unity_job_submit", "unity_job_wait",
    "udon_status", "udon_create_script", "udon_compile", "udon_attach", "udon_set_fields",
    "udon_read_vars", "udon_list_behaviours",
    "unity_harden_editor", "companion_status", "companion_setup",
    "world_project_status", "world_fix_project", "world_scaffold", "world_describe_scene", "world_validate",
    "scene_apply_spec", "scene_describe", "scene_component_whitelist", "scene_spatial_audit", "scene_survey_shots",
    "verify_scenario_schema", "verify_clientsim", "verify_build_local", "verify_multiclient_preflight",
    "verify_multiclient", "verify_vrc_logs", "verify_udon_exceptions",
    "world_build_status", "world_set_dry_run", "world_switch_platform",
    "budget_report", "budget_snapshot", "budget_diff", "budget_import_audit", "budget_apply_import_presets",
    "assets_inventory", "assets_unused", "assets_make_material", "assets_import_file", "assets_ledger",
    "geo_generate", "geo_kinds", "udon_lint",
    "udon_whitelist_query", "udon_syncable", "udon_export_whitelist",
    "project_status", "project_checkpoint", "project_checkpoints", "project_rollback",
    "open_assets_sources", "open_assets_search", "open_assets_files", "open_assets_fetch",
  ];
  // Asserting against the expected list rather than a raw count, so adding tools never fails the test.
  report("tools/list returns at least the expected surface", tools.length >= expected.length, `${tools.length} tools`);

  const missing = expected.filter((n) => !names.includes(n));
  report("every expected tool is exposed", missing.length === 0, missing.length ? `missing: ${missing.join(", ")}` : names.join(", "));

  const described = tools.filter((t) => (t.description ?? "").length > 40);
  report("tools carry real descriptions", described.length === tools.length,
    `${described.length}/${tools.length} have substantive descriptions`);

  const call = await request("tools/call", { name: "unity_health", arguments: {} });
  const text = call.result?.content?.[0]?.text ?? "";
  let parsed = null;
  try { parsed = JSON.parse(text); } catch { /* not json */ }
  report("tools/call unity_health reaches the live editor",
    Boolean(parsed?.methodCount) && !call.result?.isError,
    parsed ? `methodCount=${parsed.methodCount} jobKinds=${JSON.stringify(parsed.jobKinds)}` : text.slice(0, 200));

  const bad = await request("tools/call", { name: "udon_attach", arguments: { gameObject: "NoSuchObject", type: "Nope" } });
  const badText = bad.result?.content?.[0]?.text ?? "";
  report("a tool error surfaces as isError with a readable message",
    bad.result?.isError === true && badText.length > 0, badText.split("\n")[0].slice(0, 180));
} catch (err) {
  failed++;
  console.log(`FAIL  handshake\n      ${err.message}`);
} finally {
  child.kill();
}

console.log(`\n${passed}/${passed + failed} checks passed`);
process.exit(failed > 0 ? 1 : 0);
