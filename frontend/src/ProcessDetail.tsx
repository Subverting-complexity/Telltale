import { useEffect, useState } from 'react';
import { getProcessDetail, getProcessGroup } from './api';
import type { ProcessDetailResponse, ProcessGroupResponse } from './types';
import { ProcessTimeline } from './Timeline';
import { formatDateTime, formatElapsed } from './utils';

interface ProcessDetailProps {
  type: 'instance' | 'group';
  id?: number;
  name?: string;
  from: number;
  to: number;
  onBack: () => void;
}

export function ProcessDetail({ type, id, name, from, to, onBack }: ProcessDetailProps) {
  const [instanceData, setInstanceData] = useState<ProcessDetailResponse | null>(null);
  const [groupData, setGroupData] = useState<ProcessGroupResponse | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    if (type === 'instance' && id !== undefined) {
      getProcessDetail(id, from, to)
        .then(setInstanceData)
        .finally(() => setLoading(false));
    } else if (type === 'group' && name) {
      getProcessGroup(name, from, to)
        .then(setGroupData)
        .finally(() => setLoading(false));
    }
  }, [type, id, name, from, to]);

  if (loading) return <p>Loading...</p>;

  if (type === 'group' && groupData) {
    return (
      <div className="process-detail">
        <button className="back-btn" onClick={onBack}>&larr; Back to process list</button>
        <h2>{groupData.name}</h2>
        <p className="detail-meta">
          Resolution: {groupData.resolution} | Points: {groupData.points.length}
        </p>
        <ProcessTimeline data={groupData.points} title={groupData.name} />
      </div>
    );
  }

  if (type === 'instance' && instanceData?.info) {
    const info = instanceData.info;
    return (
      <div className="process-detail">
        <button className="back-btn" onClick={onBack}>&larr; Back to process list</button>
        <h2>{info.name} (PID {info.pid})</h2>
        <dl className="detail-info">
          {info.path && <><dt>Path</dt><dd>{info.path}</dd></>}
          {info.commandLine && <><dt>Command</dt><dd className="command-line">{info.commandLine}</dd></>}
          <dt>First seen</dt><dd>{formatDateTime(info.firstSeen)}</dd>
          <dt>Last seen</dt><dd>{formatDateTime(info.lastSeen)}</dd>
          <dt>Duration</dt><dd>{formatElapsed(info.lastSeen - info.firstSeen)}</dd>
        </dl>
        <ProcessTimeline data={instanceData.points} title={info.name} />
      </div>
    );
  }

  return <p>No data found.</p>;
}
