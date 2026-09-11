import { resolveInstance, type EditorInstance } from "./registry.js";

export interface RpcError {
  code: string;
  message: string;
  stack?: string;
}

export interface RpcResponse<T = unknown> {
  ok: boolean;
  result?: T;
  error?: RpcError;
}

export class BridgeError extends Error {
  constructor(readonly code: string, message: string, readonly stack_?: string) {
    super(message);
    this.name = "BridgeError";
  }
}

const TRANSPORT_RETRY_CODES = new Set([
  "ECONNREFUSED",
  "ECONNRESET",
  "ECONNABORTED",
  "EPIPE",
  "UND_ERR_SOCKET",
]);

/**
 * Tolerating domain reloads is this client's main job.
 *
 * Every script change tears down the editor's managed domain, which closes the listener. From out here
 * that looks like ECONNREFUSED for anywhere from a second to most of a minute. Since the bridge keeps
 * its port and token in SessionState, the right response is to re-read the registry and keep knocking
 * rather than to fail the call.
 */
export async function rpc<T = unknown>(
  method: string,
  params: Record<string, unknown> = {},
  options: { project?: string; timeoutMs?: number; reconnectWindowMs?: number } = {}
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? 60_000;
  const reconnectWindowMs = options.reconnectWindowMs ?? 90_000;
  const deadline = Date.now() + reconnectWindowMs;

  let lastTransportError: unknown = null;
  let attempt = 0;

  while (true) {
    attempt += 1;
    let instance: EditorInstance;
    try {
      instance = resolveInstance(options.project);
    } catch (err) {
      // The registry entry survives reloads, so this only fires when the editor is genuinely gone
      // or has not written its entry yet.
      if (Date.now() >= deadline) throw err;
      await sleep(750);
      continue;
    }

    try {
      return await postOnce<T>(instance, method, params, timeoutMs);
    } catch (err) {
      const code = (err as NodeJS.ErrnoException)?.code ?? "";
      const retryable =
        TRANSPORT_RETRY_CODES.has(code) ||
        (err instanceof Error && /fetch failed|socket hang up|other side closed/i.test(err.message));

      if (!retryable || Date.now() >= deadline) {
        if (err instanceof BridgeError) throw err;
        throw new Error(
          `Bridge call ${method} failed after ${attempt} attempt(s): ${(err as Error).message}. ` +
            `The editor may be reloading its script domain, shutting down, or crashed.`
        );
      }
      lastTransportError = err;
      await sleep(Math.min(250 * attempt, 1500));
    }
  }
}

async function postOnce<T>(
  instance: EditorInstance,
  method: string,
  params: Record<string, unknown>,
  timeoutMs: number
): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs + 5_000);
  try {
    const response = await fetch(`http://127.0.0.1:${instance.port}/rpc`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-agentic-token": instance.token,
      },
      body: JSON.stringify({ method, params, timeoutMs }),
      signal: controller.signal,
    });

    const payload = (await response.json()) as RpcResponse<T>;
    if (!payload.ok) {
      const error = payload.error ?? { code: "UNKNOWN", message: "Bridge returned ok:false with no error" };
      throw new BridgeError(error.code, error.message, error.stack);
    }
    return payload.result as T;
  } finally {
    clearTimeout(timer);
  }
}

/** Liveness probe that never touches Unity's main thread, so it answers even while the editor is wedged. */
export async function ping(project?: string): Promise<Record<string, unknown>> {
  const instance = resolveInstance(project);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5_000);
  try {
    const response = await fetch(`http://127.0.0.1:${instance.port}/ping`, { signal: controller.signal });
    const payload = (await response.json()) as RpcResponse<Record<string, unknown>>;
    return {
      ...(payload.result ?? {}),
      projectPath: instance.projectPath,
      projectName: instance.projectName,
      unityVersion: instance.unityVersion,
    };
  } finally {
    clearTimeout(timer);
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
