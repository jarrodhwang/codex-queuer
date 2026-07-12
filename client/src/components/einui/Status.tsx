import type { QueueStatus } from '@/api/types'

const statusLabel: Record<QueueStatus, string> = {
  Queued: 'Queued',
  Running: 'Running',
  UsageLimited: 'Usage limited',
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  CancelRequested: 'Cancelling',
  Cancelled: 'Cancelled',
}

export function StatusBadge({ status, busy = false }: { status: QueueStatus; busy?: boolean }) {
  return (
    <span className={`status-badge status-badge--${status.toLowerCase()} ${busy ? 'status-badge--busy' : ''}`}>
      {busy && <span className="status-badge-spinner" aria-hidden="true" />}
      {statusLabel[status]}
    </span>
  )
}

export function NotificationBadge({ count, label }: { count: number; label: string }) {
  return <span className="notification-badge" aria-label={label}>{count}</span>
}

export function ProgressLine({ status, percent }: { status: QueueStatus; percent: number }) {
  return (
    <div className={`progress-line progress-line--${status.toLowerCase()}`} aria-label={`${statusLabel[status]} ${percent}%`}>
      <span style={{ width: `${percent}%` }} />
    </div>
  )
}
