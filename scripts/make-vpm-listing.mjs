#!/usr/bin/env node
/**
 * Builds a VPM listing so the bridge can be installed through the VRChat Creator Companion.
 *
 *   node scripts/make-vpm-listing.mjs --base-url https://example.com/vpm
 *
 * Output, under dist-listing/:
 *
 *   index.json                     the listing a user adds to VCC
 *   com.agentic.vrcw-<version>.zip the package itself
 *
 * Both files go on any static host; VCC only needs the listing URL. The zip's SHA-256 goes into the listing,
 * because VCC verifies it — a listing that claims the wrong hash installs nothing and says little about why.
 *
 * This is the distribution path for people who live in VCC. The other one, scripts/install.mjs, links this
 * checkout straight into a project and is what the autonomous setup uses: a VCC install is a copy, so the
 * bridge's own source stops being editable in place.
 */
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(join(dirname(fileURLToPath(import.meta.url)), ".."));
const outDir = join(repo, "dist-listing");

const flags = new Map();
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  if (!argv[i].startsWith("--")) continue;
  const name = argv[i].slice(2);
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
      "Usage: node scripts/make-vpm-listing.mjs [--base-url <url>] [--id <listing id>] [--name <listing name>]",
      "",
      "  --base-url <url>  where index.json and the zip will be served from (default: a placeholder)",
      "  --id <id>         listing id, reverse-DNS (default: com.agentic.vrcw.listing)",
      "  --name <name>     listing name shown in VCC (default: Agentic VRChat World Tools)",
      "",
    ].join("\n")
  );
  process.exit(0);
}

const baseUrl = (flags.get("base-url") ?? "https://example.invalid/vpm").replace(/\/+$/, "");
const listingId = flags.get("id") ?? "com.agentic.vrcw.listing";
const listingName = flags.get("name") ?? "Agentic VRChat World Tools";

if (baseUrl.includes("example.invalid")) {
  process.stdout.write(
    "note: no --base-url given, so the listing points at a placeholder host. VCC will not install from it\n" +
      "      until the URLs are real. Re-run with --base-url once you know where this will be hosted.\n\n"
  );
}

// ---------------------------------------------------------------- the package

const packageDir = join(repo, "packages", "unity-bridge");
const manifest = JSON.parse(readFileSync(join(packageDir, "package.json"), "utf8"));
const zipName = `${manifest.name}-${manifest.version}.zip`;
const zipPath = join(outDir, zipName);

rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

/**
 * Zips the package directory.
 *
 * Not with tar: the tar on Windows is often GNU tar, which cannot write zip archives at all, and VPM wants a
 * zip. PowerShell's Compress-Archive is always present on Windows, and `zip` is the equivalent elsewhere.
 */
function zipDirectory(source, destination) {
  if (process.platform === "win32") {
    const script = `Compress-Archive -Path '${source}\\*' -DestinationPath '${destination}' -Force`;
    const run = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], {
      encoding: "utf8",
      timeout: 300_000,
    });
    if (run.error || run.status !== 0) {
      throw new Error(`Compress-Archive failed: ${run.error?.message ?? run.stderr ?? run.status}`);
    }
    return;
  }

  const run = spawnSync("zip", ["-r", "-q", destination, "."], { cwd: source, encoding: "utf8", timeout: 300_000 });
  if (run.error || run.status !== 0) {
    throw new Error(`zip failed: ${run.error?.message ?? run.stderr ?? run.status}. Install zip, or use --help.`);
  }
}

zipDirectory(packageDir, zipPath);

const bytes = readFileSync(zipPath);
const sha256 = createHash("sha256").update(bytes).digest("hex");

process.stdout.write(`packed ${zipName} (${Math.round(bytes.length / 1024)}KB, sha256 ${sha256.slice(0, 16)}…)\n`);

// ---------------------------------------------------------------- the listing

// VPM expects each version entry to be the package manifest plus url and zipSHA256. Anything else in the
// manifest is carried through untouched, which is how dependencies and the Unity version gate travel.
const versionEntry = { ...manifest, url: `${baseUrl}/${zipName}`, zipSHA256: sha256 };

const listing = {
  name: listingName,
  id: listingId,
  url: `${baseUrl}/index.json`,
  author: manifest.author ?? "",
  packages: {
    [manifest.name]: {
      versions: {
        [manifest.version]: versionEntry,
      },
    },
  },
};

writeFileSync(join(outDir, "index.json"), JSON.stringify(listing, null, 2) + "\n", "utf8");

const files = readdirSync(outDir).map((f) => `${f} (${Math.round(statSync(join(outDir, f)).size / 1024)}KB)`);
process.stdout.write(`wrote ${outDir}: ${files.join(", ")}\n`);

// A listing that ships a package whose dependencies VCC cannot resolve fails at install time with very
// little to go on, so say now what this package will ask for.
if (manifest.vpmDependencies && Object.keys(manifest.vpmDependencies).length) {
  process.stdout.write(`\nVCC will also need: ${Object.entries(manifest.vpmDependencies).map(([k, v]) => `${k} ${v}`).join(", ")}\n`);
}

process.stdout.write(
  [
    "",
    "To publish:",
    `  1. upload both files so that ${baseUrl}/index.json serves the listing`,
    "  2. in VCC: Settings -> Packages -> Add Repository, and paste that URL",
    "",
    "Note that a VCC install copies the package into the project. The autonomous setup links it instead",
    "(scripts/install.mjs), so that editing the bridge here changes what the editor loads.",
    "",
  ].join("\n")
);

if (!existsSync(join(packageDir, "package.json"))) process.exit(1);
