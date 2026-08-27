import type {
  RangeResponse, TimelineResponse, ProcessesResponse,
  ProcessDetailResponse, ProcessGroupResponse, HealthResponse,
  AlertsResponse, BaselinesResponse, HeatmapResponse, ThresholdConfig,
  WipeScope, WipeResponse,
} from './types';
import { tokenFromUrl } from './session';

const API_BASE = '/api';

async function fetchJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(url, signal ? { signal } : undefined);
  if (!res.ok) throw new Error(`API error: ${res.status} ${res.statusText}`);
  return res.json();
}

/**
 * Whether a rejection is a request that was called off rather than one that
 * failed.
 *
 * An abort has to be told apart from a real failure, because a caller answers
 * the two differently: a failure falls back to an empty answer and says so, and
 * a call-off means a newer request is already on its way and the screen should
 * be left exactly as it is.
 *
 * `fetch` rejects an aborted request with a `DOMException` named `AbortError`.
 * The name is checked rather than the type, because the test environment's fetch
 * is not always the browser's and does not always reject with a real
 * `DOMException`.
 */
export function isAbort(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: string }).name === 'AbortError';
}

export function getRange(): Promise<RangeResponse> {
  return fetchJson(`${API_BASE}/range`);
}

/**
 * `bucketMs` asks for a particular granularity. The server widens it where the
 * recording cannot serve it, and says so in the response, so passing one is a
 * request rather than an instruction.
 *
 * `signal` calls the request off. Telltale serves this window from the same
 * process that is recording the machine, so a timeline nobody is going to read
 * is not merely a wasted round trip: the query time comes out of the sampler.
 * Clicking through the detail options should leave one request running, not one
 * per click.
 */
export function getTimeline(
  from: number, to: number, bucketMs?: number | null, signal?: AbortSignal,
): Promise<TimelineResponse> {
  const params = new URLSearchParams({ from: String(from), to: String(to) });
  if (bucketMs) params.set('bucket', String(bucketMs));
  return fetchJson(`${API_BASE}/timeline?${params}`, signal);
}

export function getProcesses(
  from: number, to: number,
  opts?: { limit?: number; sort?: string; q?: string; group?: boolean }
): Promise<ProcessesResponse> {
  const params = new URLSearchParams({ from: String(from), to: String(to) });
  if (opts?.limit) params.set('limit', String(opts.limit));
  if (opts?.sort) params.set('sort', opts.sort);
  if (opts?.q) params.set('q', opts.q);
  if (opts?.group !== undefined) params.set('group', String(opts.group));
  return fetchJson(`${API_BASE}/processes?${params}`);
}

export function getProcessDetail(id: number, from: number, to: number): Promise<ProcessDetailResponse> {
  return fetchJson(`${API_BASE}/process/${id}?from=${from}&to=${to}`);
}

export function getProcessGroup(name: string, from: number, to: number): Promise<ProcessGroupResponse> {
  return fetchJson(`${API_BASE}/process-group/${encodeURIComponent(name)}?from=${from}&to=${to}`);
}

export function getHealth(): Promise<HealthResponse> {
  return fetchJson(`${API_BASE}/health`);
}

export function getAlerts(days: number): Promise<AlertsResponse> {
  return fetchJson(`${API_BASE}/alerts?days=${days}`);
}

export function getBaselines(names: string[]): Promise<BaselinesResponse> {
  return fetchJson(`${API_BASE}/baselines?names=${names.map(encodeURIComponent).join(',')}`);
}

export function getHeatmap(from: number, to: number, metric: string): Promise<HeatmapResponse> {
  return fetchJson(`${API_BASE}/heatmap?from=${from}&to=${to}&metric=${encodeURIComponent(metric)}`);
}

export function getThresholds(): Promise<ThresholdConfig> {
  return fetchJson(`${API_BASE}/thresholds`);
}

/**
 * Thrown when a wipe was refused, carrying what the application said about it so
 * the window can show the reason rather than a status code.
 */
export class WipeError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = 'WipeError';
  }
}

/**
 * Asks Telltale to throw recorded history away.
 *
 * Only the single application build serves this. It is behind the token Telltale
 * put in the address it opened this window on, so a page from anywhere else
 * cannot reach it, and the viewer executable does not offer it at all: it opens
 * the capture file read-only. A window with no token therefore fails here rather
 * than sending a request that would be refused anyway.
 */
export async function wipeCapture(what: WipeScope): Promise<WipeResponse> {
  const token = tokenFromUrl();
  if (!token) {
    throw new WipeError(
      'This window cannot delete recorded data. Open Telltale from its icon in the notification area.',
      0,
    );
  }

  const res = await fetch(`${API_BASE}/capture/wipe?s=${encodeURIComponent(token)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(what),
  });

  if (!res.ok) {
    // The application answers a refusal with a reason. A body that is missing or
    // is not the JSON we expect still has to say something, so the status stands
    // in for it rather than the failure surfacing as a parse error.
    let reason = `The request was refused (${res.status}).`;
    try {
      const body = await res.json();
      if (body && typeof body.error === 'string') reason = body.error;
    } catch {
      // Left as the status.
    }
    throw new WipeError(reason, res.status);
  }

  return res.json();
}
