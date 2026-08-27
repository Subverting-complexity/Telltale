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
  /** Width each point covers. 0 means the points are the recorded samples themselves. */
  bucketMs: number;
  /** The width that was asked for, or null when nothing was asked for. */
  bucketRequestMs: number | null;
  /** The finest width this window could have been served at. 0 means full stored detail. */
  minBucketMs: number;
  /** The finest width the tiers themselves store, ignoring how many points that comes to. */
  tierFloorMs: number;
  points: TimelinePoint[];
}

export interface ProcessGroupRow {
  name: string;
  cpuPct: number;
  privateMb: number;
  ioKb: number;
  instanceCount: number;
  path: string | null;
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
  /**
   * Which reading the rows were taken from, for a `latest` request. Null for a
   * request over the range, which has no single reading to name.
   */
  latestTs: number | null;
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
  logicalProcessors: number;
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

export interface BaselineData {
  name: string;
  avgCpu: number;
  stddevCpu: number;
  avgMemoryMb: number;
  stddevMemoryMb: number;
  avgIoKb: number;
  stddevIoKb: number;
  dataHours: number;
}

export interface BaselinesResponse {
  baselines: BaselineData[];
}

export interface HeatmapBucket {
  dayOffset: number;
  hour: number;
  avg: number;
  peak: number;
  count: number;
}

export interface HeatmapResponse {
  metric: string;
  buckets: HeatmapBucket[];
}

export interface ThresholdConfig {
  system: {
    cpuElevatedPct: number;
    cpuHighPct: number;
    memoryHighPct: number;
  };
  process: {
    cpuNotablePct: number;
    cpuElevatedPct: number;
    cpuHighPct: number;
    memoryNotableMb: number;
    memoryHighMb: number;
    ioHeavyKb: number;
    cpuSpikePct: number;
  };
}

export type ProcessSelection =
  | { type: 'group'; name: string }
  | { type: 'instance'; id: number; groupName: string }
  | { type: 'comparison'; names: string[] };

export type Theme = 'light' | 'dark' | 'system';

/** Which section of the dashboard is on screen. */
export type DashboardTab = 'overview' | 'alerts' | 'processes';

/** Which column the process table is ordered by. */
export type ProcessSort = 'cpu' | 'memory' | 'io' | 'name';

/** What to throw away: everything recorded, or one span of it. */
export type WipeScope =
  | { scope: 'all' }
  | { scope: 'range'; from: number; to: number };

/** What a wipe deleted. */
export interface WipeResponse {
  rowsDeleted: number;
  bytesFreed: number;
}
