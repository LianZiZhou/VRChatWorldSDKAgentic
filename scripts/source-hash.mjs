/**
 * One hash over the sources the committed bundles are built from.
 *
 * Shared by scripts/bundle.mjs, which records it, and scripts/install.mjs, which recomputes it to tell a
 * stale bundle from a current one. It lives in its own module precisely so that checking costs nothing:
 * importing the bundler would run it, and an install has no business rebuilding anything it was only asked
 * to inspect.
 *
 * Content, not timestamps. Staleness has to be answerable in a fresh clone, where every file carries the same
 * checkout mtime, and the recorded value should not change when nothing did.
 */
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join, sep } from "node:path";

/** Directories whose contents end up inside the bundles, relative to the repository root. */
const SOURCE_DIRS = [
  ["packages", "vrcw-mcp", "src"],
  ["packages", "supervisor", "src"],
];

export function hashSources(repoRoot) {
  const hash = createHash("sha256");

  const walk = (dir) => {
    if (!existsSync(dir)) return;
    // Sorted, so the hash does not depend on directory enumeration order.
    const entries = readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name));
    for (const entry of entries) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (/[.](ts|mjs|js|json)$/.test(entry.name)) {
        hash.update(full.slice(repoRoot.length).split(sep).join("/"));
        hash.update(readFileSync(full));
      }
    }
  };

  for (const parts of SOURCE_DIRS) walk(join(repoRoot, ...parts));
  return hash.digest("hex");
}
