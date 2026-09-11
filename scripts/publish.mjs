#!/usr/bin/env node
/**
 * Prepares the two Node packages for npm, and publishes them only when told to twice.
 *
 *   node scripts/publish.mjs --dry-run          build, pack, and report what would go out
 *   node scripts/publish.mjs --confirm          actually publish
 *
 * Publishing is irreversible in the way that matters: a version number, once taken on the public registry,
 * stays taken, and an unscoped name claims ground nobody can hand back. So the default is a dry run, the real
 * thing needs --confirm, and the packages stay marked private in git — this script refuses to publish one
 * that still is, rather than quietly unsetting it.
 *
 * For most people this is the wrong path anyway: the toolchain is a repository to clone plus
 * scripts/install.mjs, and the Unity half ships through a VPM listing (scripts/make-vpm-listing.mjs). npm
 * matters only for running the MCP server with npx, without a checkout.
 */
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(join(dirname(fileURLToPath(import.meta.url)), ".."));

const PACKAGES = [
  { dir: "packages/vrcw-mcp", what: "MCP server" },
  { dir: "packages/supervisor", what: "editor supervisor" },
];

const argv = process.argv.slice(2);
const confirm = argv.includes("--confirm");
const dryRun = !confirm;

if (argv.includes("--help")) {
  process.stdout.write(
    [
      "Usage: node scripts/publish.mjs [--dry-run | --confirm] [--tag <npm tag>] [--otp <code>]",
      "",
      "  --dry-run      default: build, pack, and report. Nothing is published.",
      "  --confirm      publish to the registry for real.",
      "  --tag <tag>    npm dist-tag (default: latest)",
      "  --otp <code>   two-factor code, if the account requires one",
      "",
    ].join("\n")
  );
  process.exit(0);
}

const tagIndex = argv.indexOf("--tag");
const distTag = tagIndex >= 0 ? argv[tagIndex + 1] : "latest";
const otpIndex = argv.indexOf("--otp");
const otp = otpIndex >= 0 ? argv[otpIndex + 1] : null;

function run(cmd, args, opts = {}) {
  const windows = process.platform === "win32";
  const quote = (v) => (/[\s&|<>^"]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v);
  const result = windows
    ? spawnSync([cmd, ...args].map(quote).join(" "), {
        encoding: "utf8",
        cwd: opts.cwd ?? repo,
        timeout: opts.timeout ?? 600_000,
        shell: true,
      })
    : spawnSync(cmd, args, { encoding: "utf8", cwd: opts.cwd ?? repo, timeout: opts.timeout ?? 600_000 });
  return {
    ok: !result.error && result.status === 0,
    out: `${result.stdout ?? ""}${result.stderr ?? ""}`.trim(),
  };
}

let problems = 0;

function say(ok, message, detail) {
  if (!ok) problems++;
  process.stdout.write(`[${ok ? "  ok  " : " FAIL "}] ${message}\n`);
  if (detail) process.stdout.write(`         ${detail.split("\n").slice(0, 6).join("\n         ")}\n`);
}

// ---------------------------------------------------------------- preflight

{
  const status = run("git", ["status", "--porcelain"]);
  const dirty = status.out.split(/\r?\n/).filter(Boolean);
  // Publishing from a dirty tree means the published artifact corresponds to no commit anybody can check out.
  say(dirty.length === 0, `working tree ${dirty.length === 0 ? "is clean" : `has ${dirty.length} uncommitted change(s)`}`,
    dirty.length ? dirty.slice(0, 8).join("\n") : undefined);
}

{
  const built = run("npm", ["run", "build"], { timeout: 900_000 });
  say(built.ok, "npm run build", built.ok ? undefined : built.out.slice(-800));
}

{
  const bundled = run("npm", ["run", "bundle"], { timeout: 900_000 });
  say(bundled.ok, "npm run bundle (committed artifacts refreshed)", bundled.ok ? undefined : bundled.out.slice(-800));
}

// ---------------------------------------------------------------- per package

for (const pkg of PACKAGES) {
  const dir = join(repo, pkg.dir);
  const manifestPath = join(dir, "package.json");
  if (!existsSync(manifestPath)) {
    say(false, `${pkg.dir}: no package.json`);
    continue;
  }

  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  const label = `${manifest.name}@${manifest.version} (${pkg.what})`;

  if (!existsSync(join(dir, "dist", "index.js"))) {
    say(false, `${label}: dist/index.js is missing, so the package would publish unusable`);
    continue;
  }

  const packed = run("npm", ["pack", "--dry-run", "--json"], { cwd: dir });
  let files = null;
  try {
    files = JSON.parse(packed.out.slice(packed.out.indexOf("[")))[0];
  } catch {
    files = null;
  }
  say(
    packed.ok && Boolean(files),
    `${label}: packs cleanly`,
    files ? `${files.entryCount} files, ${Math.round((files.unpackedSize ?? 0) / 1024)}KB unpacked` : packed.out.slice(-400)
  );

  if (dryRun) continue;

  if (manifest.private) {
    say(false, `${label}: still marked "private": true`,
      'remove that field in a commit if this package is meant to be public — this script will not edit it for you');
    continue;
  }

  const args = ["publish", "--access", "public", "--tag", distTag];
  if (otp) args.push("--otp", otp);
  const published = run("npm", args, { cwd: dir, timeout: 900_000 });
  say(published.ok, `${label}: published with tag ${distTag}`, published.ok ? undefined : published.out.slice(-800));
}

// ---------------------------------------------------------------- outcome

process.stdout.write("\n");
if (dryRun) {
  process.stdout.write(
    problems
      ? `Dry run found ${problems} problem(s). Fix those before publishing.\n`
      : "Dry run clean. Publish with: node scripts/publish.mjs --confirm\n"
  );
} else {
  process.stdout.write(problems ? `Publish finished with ${problems} problem(s).\n` : "Published.\n");
}

process.exitCode = problems ? 1 : 0;
