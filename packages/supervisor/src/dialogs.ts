/**
 * Modal-dialog detection for the Unity editor.
 *
 * A modal dialog is the worst thing that can happen to an unattended run: it halts Unity's update loop,
 * which halts the bridge's main-thread pump, so every call returns EDITOR_BUSY and nothing recovers. It
 * happened for real — Unity's "detected external changes, reload?" prompt blocked a full test run until a
 * human clicked Reload.
 *
 * Prevention lives in the editor (bridge.harden_editor turns Asset Pipeline Auto Refresh on, which is what
 * creates that prompt in the first place). This module is the second line: when the pump has stalled, say
 * *what* is on screen instead of leaving the agent to guess, and optionally answer dialogs that are known
 * to be safe to answer.
 */
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const scriptsDir = join(dirname(fileURLToPath(import.meta.url)), "..", "scripts");

export interface WindowInfo {
  handle: number;
  title: string;
  className: string;
  visible: boolean;
  enabled: boolean;
  hasOwner: boolean;
}

/**
 * Dialog titles this tool will answer on its own.
 *
 * Each entry is a dialog whose default button is both predictable and the outcome an automated run wants.
 * Anything not listed is reported and left alone, because pressing the default button of an unrecognised
 * dialog is a way to agree to something nobody asked for.
 */
export const DEFAULT_DISMISS_ALLOWLIST: Array<{ pattern: RegExp; note: string }> = [
  {
    // Observed verbatim: "The open scene(s) have been modified externally". Unity raises this when the
    // scene file on disk changes while that scene is open — including when something re-imports the
    // scene Unity itself just saved. Reloading from disk is correct: disk holds the newer version.
    pattern: /modified externally|external(?:ly)? chang|out of date|detected|reload/i,
    note: "Scene/asset external-modification prompt. Default action reloads from disk, which is the newer version.",
  },
  {
    pattern: /verify saving assets|save assets/i,
    note: "Asset-save confirmation list. Default action saves.",
  },
];

export function listWindows(pid: number): WindowInfo[] {
  if (process.platform !== "win32" || !Number.isInteger(pid) || pid <= 0) return [];

  const result = spawnSync(
    "powershell",
    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", join(scriptsDir, "list-windows.ps1"), "-ProcessId", String(pid)],
    { encoding: "utf8", timeout: 20_000 }
  );

  const out = (result.stdout ?? "").trim();
  if (!out) return [];
  try {
    const parsed = JSON.parse(out);
    const array = Array.isArray(parsed) ? parsed : [parsed];
    return array.map((w: any) => ({
      handle: Number(w.Handle ?? 0),
      title: String(w.Title ?? ""),
      className: String(w.ClassName ?? ""),
      visible: Boolean(w.Visible),
      enabled: Boolean(w.Enabled),
      hasOwner: Boolean(w.HasOwner),
    }));
  } catch {
    return [];
  }
}

/**
 * Windows that look like a dialog rather than the editor itself.
 *
 * Unity's main window carries the project/scene name and owns nothing; its dialogs are owned top-level
 * windows. Untitled windows are tooltips and popups, not things a human would click.
 */
export function findDialogs(pid: number, mainWindowTitle?: string): WindowInfo[] {
  return listWindows(pid).filter((w) => {
    if (!w.title.trim()) return false;
    if (mainWindowTitle && w.title === mainWindowTitle) return false;
    // The editor's own main window is the one without an owner.
    return w.hasOwner;
  });
}

export function matchAllowlist(title: string): { allowed: boolean; note?: string } {
  for (const entry of DEFAULT_DISMISS_ALLOWLIST) {
    if (entry.pattern.test(title)) return { allowed: true, note: entry.note };
  }
  return { allowed: false };
}

export interface DismissResult {
  handle: number;
  title: string;
  attempted: boolean;
  focused?: boolean;
  sent?: boolean;
  windowStillExists?: boolean;
  reason?: string;
}

export function dismissDialog(dialog: WindowInfo, key = "{ENTER}"): DismissResult {
  if (process.platform !== "win32") {
    return { handle: dialog.handle, title: dialog.title, attempted: false, reason: "not supported off Windows" };
  }

  const result = spawnSync(
    "powershell",
    [
      "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
      "-File", join(scriptsDir, "dismiss-window.ps1"),
      "-Handle", String(dialog.handle),
      "-Key", key,
    ],
    { encoding: "utf8", timeout: 20_000 }
  );

  try {
    const parsed = JSON.parse((result.stdout ?? "").trim());
    return {
      handle: dialog.handle,
      title: dialog.title,
      attempted: true,
      focused: Boolean(parsed.focused),
      sent: Boolean(parsed.sent),
      windowStillExists: Boolean(parsed.windowStillExists),
      reason: parsed.reason,
    };
  } catch {
    return {
      handle: dialog.handle,
      title: dialog.title,
      attempted: true,
      reason: `could not parse dismiss output: ${(result.stderr ?? "").slice(0, 200)}`,
    };
  }
}
