import type {
  RangeResponse, TimelineResponse, ProcessesResponse,
  ProcessDetailResponse, ProcessGroupResponse, HealthResponse,
  AlertsResponse,
} from './types';

const API_BASE = '/api';

async function fetchJson<T>(url: string): Promise<T> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`API error: ${res.status} ${res.statusText}`);
  return res.json();
}

export function getRange(): Promise<RangeResponse> {
  return fetchJson(`${API_BASE}/range`);
}

export function getTimeline(from: number, to: number): Promise<TimelineResponse> {
  return fetchJson(`${API_BASE}/timeline?from=${from}&to=${to}`);
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
