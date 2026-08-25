import { useEffect, useState } from 'react';
import { getProcessDetail, getProcessGroup, getProcesses } from './api';
import type { ProcessDetailResponse, ProcessGroupResponse, ProcessInstanceRow, ThresholdConfig } from './types';
import { ProcessTimeline } from './Timeline';
import { formatDateTime, formatElapsed, formatSize, formatCpu, formatIo } from './utils';

interface ProcessDetailProps {
  type: 'instance' | 'group';
  id?: number;
  name?: string;
  groupName?: string;
  from: number;
  to: number;
  onBack: () => void;
  onSelectInstance?: (id: number, groupName: string) => void;
  thresholds?: ThresholdConfig | null;
}

type InstanceSort = 'cpu' | 'memory' | 'io' | 'pid';
type SortDir = 'asc' | 'desc';

export function ProcessDetail({
  type, id, name, groupName, from, to, onBack,
  onSelectInstance, thresholds,
}: ProcessDetailProps) {
  const [instanceData, setInstanceData] = useState<ProcessDetailResponse | null>(null);
  const [groupData, setGroupData] = useState<ProcessGroupResponse | null>(null);
  const [instances, setInstances] = useState<ProcessInstanceRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [instanceSort, setInstanceSort] = useState<InstanceSort>('cpu');
  const [instanceSortDir, setInstanceSortDir] = useState<SortDir>('desc');

  useEffect(() => {
    setLoading(true);
    setError(null);
    if (type === 'instance' && id !== undefined) {
      getProcessDetail(id, from, to)
        .then(setInstanceData)
        .catch(() => setError('Could not load process data.'))
        .finally(() => setLoading(false));
    } else if (type === 'group' && name) {
      const groupPromise = getProcessGroup(name, from, to)
        .then(setGroupData)
        .catch(() => setError('Could not load process group data.'));

      const instancesPromise = getProcesses(from, to, { group: false, q: name, limit: 100 })
        .then(procs => {
          const ungrouped = procs.processes as ProcessInstanceRow[];
          setInstances(ungrouped.filter(p => p.name === name));
        })
        .catch(() => {});

      Promise.allSettled([groupPromise, instancesPromise])
        .finally(() => setLoading(false));
    }
  }, [type, id, name, from, to]);

  function toggleInstanceSort(col: InstanceSort) {
    if (instanceSort === col) {
      setInstanceSortDir(d => d === 'desc' ? 'asc' : 'desc');
    } else {
      setInstanceSort(col);
      setInstanceSortDir('desc');
    }
  }

  function sortIcon(col: InstanceSort) {
    if (instanceSort !== col) return '';
    return instanceSortDir === 'desc' ? ' ▼' : ' ▲';
  }

  const sortedInstances = [...instances].sort((a, b) => {
    const dir = instanceSortDir === 'desc' ? -1 : 1;
    switch (instanceSort) {
      case 'cpu': return (a.cpuPct - b.cpuPct) * dir;
      case 'memory': return (a.privateMb - b.privateMb) * dir;
      case 'io': return (a.ioKb - b.ioKb) * dir;
      case 'pid': return (a.pid - b.pid) * dir;
      default: return 0;
    }
  });

  const displayName = name ?? groupName ?? (instanceData?.info?.name) ?? 'Process';

  if (loading) {
    return (
      <div className="process-detail">
        <Breadcrumbs name={displayName} onBack={onBack} groupName={groupName} type={type} />
        <p className="loading">Loading...</p>
      </div>
    );
  }

  if (type === 'group' && groupData) {
    return (
      <div className="process-detail">
        <Breadcrumbs name={groupData.name} onBack={onBack} type={type} />

        <h2>{groupData.name}</h2>
        <p className="detail-meta">
          Resolution: {groupData.resolution} | Points: {groupData.points.length}
          {instances.length > 0 && ` | Instances: ${instances.length}`}
        </p>

        <ProcessTimeline data={groupData.points} title={groupData.name} thresholds={thresholds} />

        {sortedInstances.length > 0 && (
          <div className="instance-list">
            <h3>Instances</h3>
            <div className="process-table-wrapper" role="region" aria-label="Process instances" tabIndex={0}>
              <table className="process-table">
                <caption className="sr-only">Instances of {groupData.name}</caption>
                <thead>
                  <tr>
                    <th scope="col" style={{ textAlign: 'left' }}>
                      <button className="sort-btn" onClick={() => toggleInstanceSort('pid')}>
                        PID{sortIcon('pid')}
                      </button>
                    </th>
                    <th scope="col" style={{ textAlign: 'left' }}>Path</th>
                    <th scope="col">
                      <button className="sort-btn" onClick={() => toggleInstanceSort('cpu')}>
                        CPU %{sortIcon('cpu')}
                      </button>
                    </th>
                    <th scope="col">
                      <button className="sort-btn" onClick={() => toggleInstanceSort('memory')}>
                        Memory{sortIcon('memory')}
                      </button>
                    </th>
                    <th scope="col">
                      <button className="sort-btn" onClick={() => toggleInstanceSort('io')}>
                        I/O{sortIcon('io')}
                      </button>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {sortedInstances.map(inst => (
                    <tr
                      key={inst.id}
                      className="process-row"
                      onClick={() => onSelectInstance?.(inst.id, groupData.name)}
                      onKeyDown={e => { if (e.key === 'Enter') onSelectInstance?.(inst.id, groupData.name); }}
                      tabIndex={0}
                      role="button"
                      aria-label={`View instance PID ${inst.pid}`}
                    >
                      <td style={{ textAlign: 'left' }}>{inst.pid}</td>
                      <td style={{ textAlign: 'left' }} className="instance-path">
                        {inst.path ?? '-'}
                      </td>
                      <td>{formatCpu(inst.cpuPct)}</td>
                      <td>{formatSize(inst.privateMb)}</td>
                      <td>{formatIo(inst.ioKb)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    );
  }

  if (type === 'instance' && instanceData?.info) {
    const info = instanceData.info;
    return (
      <div className="process-detail">
        <Breadcrumbs name={`PID ${info.pid}`} onBack={onBack} groupName={groupName} type={type} />

        <h2>{info.name} (PID {info.pid})</h2>
        <dl className="detail-info">
          {info.path && <><dt>Path</dt><dd>{info.path}</dd></>}
          {info.commandLine && <><dt>Command</dt><dd className="command-line">{info.commandLine}</dd></>}
          <dt>First seen</dt><dd>{formatDateTime(info.firstSeen)}</dd>
          <dt>Last seen</dt><dd>{formatDateTime(info.lastSeen)}</dd>
          <dt>Duration</dt><dd>{formatElapsed(info.lastSeen - info.firstSeen)}</dd>
        </dl>
        <ProcessTimeline data={instanceData.points} title={info.name} thresholds={thresholds} />
      </div>
    );
  }

  return (
    <div className="process-detail">
      <Breadcrumbs name={displayName} onBack={onBack} groupName={groupName} type={type} />
      <p className="no-data-msg">{error ?? 'No data found for this process in the selected range.'}</p>
    </div>
  );
}

function Breadcrumbs({ name, onBack, groupName, type }: {
  name: string;
  onBack: () => void;
  groupName?: string;
  type: 'instance' | 'group';
}) {
  return (
    <nav className="detail-breadcrumbs" aria-label="Navigation">
      <button className="breadcrumb-link" onClick={onBack}>Dashboard</button>
      {type === 'instance' && groupName && (
        <>
          <span className="breadcrumb-sep" aria-hidden="true">/</span>
          <button className="breadcrumb-link" onClick={onBack}>{groupName}</button>
        </>
      )}
      <span className="breadcrumb-sep" aria-hidden="true">/</span>
      <span className="breadcrumb-current">{name}</span>
    </nav>
  );
}
