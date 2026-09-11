/**
 * Fetching assets from sources whose licence is unambiguous.
 *
 * An autonomous agent assembling a world from the open web creates a licensing problem that cannot be fixed
 * later: nobody can tell afterwards where a texture came from or whether it may be shipped. So this module is
 * built around refusal rather than capability — only hosts on an allowlist are reachable, and each carries the
 * licence its operator states for all its content, recorded with every download.
 *
 * `direct` exists for everything else, and deliberately requires the caller to name the licence. That makes
 * the claim explicit and attributable instead of implied.
 *
 * Download is separate from import on purpose: this puts a file on disk with a checksum, and
 * assets_import_file brings it into the project with its provenance. Nothing enters a project without a
 * recorded source and licence.
 */
import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

export interface OpenSource {
  id: string;
  name: string;
  /** Licence the operator states for the content this tool can reach. */
  licence: string;
  hosts: string[];
  notes: string;
}

/**
 * Hosts this tool may contact.
 *
 * Each entry is a source that publishes its whole relevant catalogue under one licence, which is what makes
 * an automated licence claim defensible. A source with mixed or per-asset licensing does not belong here —
 * it belongs behind `direct`, where a human states the licence for the specific asset.
 */
export const OPEN_SOURCES: OpenSource[] = [
  {
    id: "polyhaven",
    name: "Poly Haven",
    licence: "CC0-1.0",
    hosts: ["api.polyhaven.com", "dl.polyhaven.org"],
    notes: "HDRIs, textures and models, all CC0. Attribution is not required but is recorded anyway.",
  },
  {
    id: "ambientcg",
    name: "ambientCG",
    licence: "CC0-1.0",
    hosts: ["ambientcg.com", "acg-media.struffelproductions.com", "api.ambientcg.com"],
    notes: "PBR material sets, CC0.",
  },
];

function hostOf(url: string): string {
  return new URL(url).hostname.toLowerCase();
}

function allowedSourceForHost(host: string): OpenSource | null {
  for (const source of OPEN_SOURCES) {
    if (source.hosts.some((h) => host === h || host.endsWith(`.${h}`))) return source;
  }
  return null;
}

export function listSources(): Record<string, unknown> {
  return {
    sources: OPEN_SOURCES,
    direct: {
      id: "direct",
      description:
        "Any https URL, but the caller must state the licence. Use this when the source is not a single-licence " +
        "catalogue — the licence claim then belongs to whoever made it, rather than being inferred.",
    },
    refused:
      "Hosts outside this list are refused. Asset stores and marketplaces (Booth, the Unity Asset Store, " +
      "Gumroad and similar) sell per-asset licences that an automated run cannot evaluate or pay for, so they " +
      "are deliberately unreachable.",
  };
}

/**
 * Searches Poly Haven's public catalogue. Read-only, no credentials.
 */
export async function searchPolyHaven(
  query: string,
  kind: "hdris" | "textures" | "models",
  limit: number
): Promise<Record<string, unknown>> {
  const response = await fetch(`https://api.polyhaven.com/assets?t=${encodeURIComponent(kind)}`, {
    headers: { accept: "application/json" },
  });
  if (!response.ok) throw new Error(`Poly Haven returned ${response.status} ${response.statusText}`);

  const catalogue = (await response.json()) as Record<string, any>;
  const needle = query.trim().toLowerCase();
  const matches: Array<Record<string, unknown>> = [];

  for (const [id, asset] of Object.entries(catalogue)) {
    const haystack = `${id} ${(asset?.tags ?? []).join(" ")} ${(asset?.categories ?? []).join(" ")}`.toLowerCase();
    if (needle && !haystack.includes(needle)) continue;
    matches.push({
      id,
      name: asset?.name ?? id,
      categories: asset?.categories ?? [],
      tags: (asset?.tags ?? []).slice(0, 8),
      filesEndpoint: `https://api.polyhaven.com/files/${id}`,
    });
    if (matches.length >= limit) break;
  }

  return {
    source: "polyhaven",
    licence: "CC0-1.0",
    kind,
    query,
    matchCount: matches.length,
    matches,
    next: "Call open_assets_files with the id to see the available resolutions and formats, then open_assets_fetch to download one.",
  };
}

/** Lists the concrete downloadable files for one Poly Haven asset. */
export async function polyHavenFiles(id: string): Promise<Record<string, unknown>> {
  const response = await fetch(`https://api.polyhaven.com/files/${encodeURIComponent(id)}`, {
    headers: { accept: "application/json" },
  });
  if (!response.ok) throw new Error(`Poly Haven returned ${response.status} for asset "${id}"`);

  const files = (await response.json()) as Record<string, any>;
  const options: Array<Record<string, unknown>> = [];

  // The API nests as type -> resolution -> format -> { url, size }. Flatten just enough to choose from,
  // preferring smaller resolutions since a world has a size budget.
  const walk = (node: any, path: string[]) => {
    if (!node || typeof node !== "object") return;
    if (typeof node.url === "string") {
      options.push({ path: path.join("/"), url: node.url, bytes: node.size ?? null });
      return;
    }
    for (const [key, value] of Object.entries(node)) walk(value, [...path, key]);
  };
  walk(files, []);

  options.sort((a, b) => Number(a.bytes ?? Infinity) - Number(b.bytes ?? Infinity));
  return {
    source: "polyhaven",
    licence: "CC0-1.0",
    id,
    optionCount: options.length,
    options: options.slice(0, 40),
    hint: "Smallest first. A 1K texture is usually the right choice for a world, and is required for Quest.",
  };
}

export interface FetchResult {
  file: string;
  url: string;
  host: string;
  source: string;
  licence: string;
  bytes: number;
  sha256: string;
  nextStep: string;
}

/**
 * Downloads one file to a staging directory.
 *
 * Refuses any host not on the allowlist, and refuses plain http. Nothing is written into the Unity project
 * here — assets_import_file does that, and requires the provenance this returns.
 */
export async function fetchOpenAsset(
  url: string,
  declaredLicence: string | undefined,
  stagingDir: string | undefined,
  maxBytes: number
): Promise<FetchResult> {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    throw new Error(`Not a valid URL: ${url}`);
  }
  if (parsed.protocol !== "https:")
    throw new Error(`Refusing ${parsed.protocol} — only https is allowed, so the transfer cannot be tampered with.`);

  const host = parsed.hostname.toLowerCase();
  const known = allowedSourceForHost(host);

  let source: string;
  let licence: string;
  if (known) {
    source = known.name;
    licence = known.licence;
  } else {
    if (!declaredLicence)
      throw new Error(
        `${host} is not an allowlisted open-licence source. Pass licence to take responsibility for the claim, ` +
          `or use one of: ${OPEN_SOURCES.map((s) => s.hosts[0]).join(", ")}. ` +
          `Asset marketplaces are deliberately unreachable: their per-asset licences cannot be evaluated or paid ` +
          `for by an automated run.`
      );
    source = host;
    licence = declaredLicence;
  }

  const response = await fetch(url, { redirect: "follow" });
  if (!response.ok) throw new Error(`Download failed: ${response.status} ${response.statusText} for ${url}`);

  const declaredLength = Number(response.headers.get("content-length") ?? 0);
  if (declaredLength > maxBytes)
    throw new Error(`Refusing a ${(declaredLength / 1048576).toFixed(1)}MB download; maxBytes is ${maxBytes}.`);

  const bytes = Buffer.from(await response.arrayBuffer());
  if (bytes.length > maxBytes)
    throw new Error(`Download was ${(bytes.length / 1048576).toFixed(1)}MB, over the ${maxBytes}-byte limit.`);

  const dir = stagingDir ?? join(tmpdir(), "agentic-open-assets");
  mkdirSync(dir, { recursive: true });

  const name = decodeURIComponent(parsed.pathname.split("/").filter(Boolean).pop() ?? "download.bin");
  const file = join(dir, name);
  writeFileSync(file, bytes);

  return {
    file: file.replace(/\\/g, "/"),
    url,
    host,
    source,
    licence,
    bytes: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
    nextStep:
      `Call assets_import_file with sourcePath="${file.replace(/\\/g, "/")}", source="${source}", ` +
      `licence="${licence}", sourceUrl="${url}" to bring it into the project with its provenance recorded.`,
  };
}
