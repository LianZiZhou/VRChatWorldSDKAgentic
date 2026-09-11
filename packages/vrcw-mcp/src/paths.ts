/**
 * Locates the toolchain's own files.
 *
 * The server runs from two different layouts: `packages/vrcw-mcp/dist/index.js` during development, and a
 * single bundled file under `bundled/` in a released checkout. A relative "../.." works in one and points
 * at nonsense in the other, so the repository root is found by looking for a marker instead of by counting
 * directories.
 */
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const MARKER = join("packages", "unity-bridge", "package.json");

let cached: string | null = null;

export function repoRoot(): string {
  if (cached) return cached;

  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 8; i++) {
    if (existsSync(join(dir, MARKER))) return (cached = dir);
    const parent = resolve(dir, "..");
    if (parent === dir) break;
    dir = parent;
  }

  // Fall back to the working directory, which is the repo root when run through npm scripts.
  if (existsSync(join(process.cwd(), MARKER))) return (cached = process.cwd());
  throw new Error(
    `Could not locate the VRChatWorldSDKAgentic checkout: no ${MARKER} above ` +
      `${dirname(fileURLToPath(import.meta.url))} or in ${process.cwd()}.`
  );
}

export function analyzerRoot(): string {
  return join(repoRoot(), "packages", "udon-analyzer");
}

export function analyzerExe(): string {
  return join(
    analyzerRoot(),
    "bin",
    "Release",
    "net8.0",
    process.platform === "win32" ? "udon-analyzer.exe" : "udon-analyzer"
  );
}
