export interface TimelinePoint {
  ts: number;
  cpuPct: number | null;
  memoryAvailMb: number | null;
  commitMb: number | null;
  hardFaults: number | null;
  diskReadMs: number | null;
  diskWriteMs: number | null;
  memoryTotalMb: number | null;
  diskBusyPct: number | null;
  netKbps: number | null;
  gpuBusyPct: number | null;
}

export interface TimelineResponse {
  resolution: string;
  points: TimelinePoint[];
}

export interface ProcessGroupRow {
  name: string;
  cpuPct: number;
  privateMb: number;
  ioKb: number;
  instanceCount: number;
}

export interface ProcessInstanceRow {
  id: number;
  pid: number;
  name: string;
  path: string | null;
  cpuPct: number;
  privateMb: number;
  ioKb: number;
}

export interface ProcessesResponse {
  grouped: boolean;
  processes: ProcessGroupRow[] | ProcessInstanceRow[];
}

export interface ProcessPoint {
  ts: number;
  cpuPct: number | null;
  privateMb: number | null;
  workingSetMb: number | null;
  ioKb: number | null;
  instanceCount?: number;
}

export interface ProcessInfo {
  pid: number;
  name: string;
  path: string | null;
  commandLine: string | null;
  firstSeen: number;
  lastSeen: number;
}

export interface ProcessDetailResponse {
  info: ProcessInfo | null;
  resolution: string;
  points: ProcessPoint[];
}

export interface ProcessGroupResponse {
  name: string;
  resolution: string;
  points: ProcessPoint[];
}

export interface HealthResponse {
  collectorRunning: boolean;
  lastSampleTs: number;
  sampleCostMs: number;
  processCount: number;
  storedCount: number;
  dbSizeMb: number;
}

export interface RangeResponse {
  min: number | null;
  max: number | null;
}

export interface AlertProcess {
  name: string;
  avgCpuPct: number;
  peakCpuPct: number;
  peakMemoryMb: number;
  totalIoKb: number;
  sampleCount: number;
  instanceCount: number;
  firstTs: number;
  lastTs: number;
  reasons: string[];
}

export interface AlertsResponse {
  period: number;
  alerts: AlertProcess[];
}

export type ViewScale = 'year' | 'month' | 'week' | 'day';

export interface ViewState {
  scale: ViewScale;
  year: number;
  month?: number;
  day?: number;
}

export type Theme = 'light' | 'dark' | 'system';
