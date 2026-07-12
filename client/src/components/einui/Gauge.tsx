import { cn } from '@/lib/utils'

type GaugeProps = {
  label: string
  value: number | null
  className?: string
  tone?: 'default' | 'limited'
}

const radius = 42
const circumference = 2 * Math.PI * radius

export function Gauge({ label, value, className, tone = 'default' }: GaugeProps) {
  const percentage = value === null ? null : Math.round(Math.max(0, Math.min(100, value)))
  const progress = percentage === null ? 0 : (percentage / 100) * circumference
  const displayValue = percentage === null ? '—' : `${percentage}%`

  return (
    <div
      className={cn('ein-gauge', `ein-gauge--${tone}`, percentage === null && 'ein-gauge--unknown', className)}
      role="progressbar"
      aria-label={`${label} ${percentage === null ? 'percentage unavailable' : `${percentage}% left`}`}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={percentage ?? undefined}
    >
      <svg viewBox="0 0 100 100" aria-hidden="true">
        <circle className="ein-gauge-track" cx="50" cy="50" r={radius} />
        {percentage !== null && (
          <circle
            className="ein-gauge-value"
            cx="50"
            cy="50"
            r={radius}
            strokeDasharray={`${progress} ${circumference - progress}`}
          />
        )}
      </svg>
      <span>{displayValue}</span>
    </div>
  )
}
