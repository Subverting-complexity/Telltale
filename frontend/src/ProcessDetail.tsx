import { useEffect, useState } from 'react';
import { getProcessDetail, getProcessGroup, getProcesses } from './api';
import type { ProcessDetailResponse, ProcessGroupResponse, ProcessInstanceRow, ThresholdConfig } from './types';
import { ProcessTimeline } from './Timeline';
import { formatDateTime, formatElapsed, formatSize, formatCpu } from './utils';

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

export function ProcessDetail({
  type, id, name, groupName, from, to, onBack,
  onSelectInstance, thresholds,
}: ProcessDetailProps) {
  const [instanceData, setInstanceData] = useState<ProcessDetailResponse | null>(null);
  const [groupData, setGroupData] = useState<ProcessGroupResponse | null>(null);
  const [instances, setInstances] = useState<ProcessInstanceRow[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    if (type === 'instance' && id !== undefined) {
      getProcessDetail(id, from, to)
        .then(setInstanceData)
        .finally(() => setLoading(false));
    } else if (type === 'group' && name) {
      Promise.all([
        getProcessGroup(name, from, to),
        getProcesses(from, to, { group: false, q: name, limit: 100 }),
      ]).then(([group, procs]) => {
        setGroupData(group);
        const ungrouped = procs.processes as ProcessInstanceRow[];
        setInstances(ungrouped.filter(p => p.name === name));
      }).finally(() => setLoading(false));
    }
  }, [type, id, name, from, to]);

  if (loading) return <p>Loading...</p>;

  if (type === 'group' && groupData) {
    return (
      <div className="process-detail">
        <nav className="detail-breadcrumbs" aria-label="Navigation">
          <button className="breadcrumb-link" onClick={onBack}>Dashboard</button>
          <span className="breadcrumb-sep">&gt;</span>
          <span className="breadcrumb-current">{groupData.name}</span>
        </nav>

        <h2>{groupData.name}</h2>
        <p className="detail-meta">
          Resolution: {groupData.resolution} | Points: {groupData.points.length}
          {instances.length > 0 && ` | Instances: ${instances.length}`}
        </p>

        <ProcessTimeline data={groupData.points} title={groupData.name} thresholds={thresholds} />

        {instances.length > 0 && (
          <div className="instance-list">
            <h3>Instances</h3>
            <div className="process-table-wrapper" role="region" aria-label="Process instances" tabIndex={0}>
              <table className="process-table">
                <caption className="sr-only">Instances of {groupData.name}</caption>
                <thead>
                  <tr>
                    <th scope="col" style={{ textAlign: 'left' }}>PID</th>
                    <th scope="col" style={{ textAlign: 'left' }}>Path</th>
                    <th scope="col">CPU %</th>
                    <th scope="col">Memory</th>
                    <th scope="col">I/O</th>
                  </tr>
                </thead>
                <tbody>
                  {instances.map(inst => (
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
                      <td>{inst.ioKb != null ? formatSize(inst.ioKb) : '-'}</td>
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
        <nav className="detail-breadcrumbs" aria-label="Navigation">
          <button className="breadcrumb-link" onClick={onBack}>Dashboard</button>
          {groupName && (
            <>
              <span className="breadcrumb-sep">&gt;</span>
              <button className="breadcrumb-link" onClick={onBack}>
                {groupName}
              </button>
            </>
          )}
          <span className="breadcrumb-sep">&gt;</span>
          <span className="breadcrumb-current">PID {info.pid}</span>
        </nav>

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

  return <p>No data found.</p>;
}
