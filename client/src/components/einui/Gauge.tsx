import { useId } from 'react'
import { cn } from '@/lib/utils'

type GaugeProps = {
  label: string
  value: number | null
  className?: string
  tone?: 'default' | 'limited'
}

const radius = 40

export function Gauge({ label, value, className, tone = 'default' }: GaugeProps) {
  const gradientId = useId().replace(/:/g, '')
  const percentage = value === null ? null : Math.round(Math.max(0, Math.min(100, value)))
  const displayValue = percentage === null ? '—' : `${percentage}%`

  return (
    <div
      className={cn('ein-gauge', `ein-gauge--${tone}`, percentage === null && 'ein-gauge--unknown', className)}
      role="progressbar"
      aria-label={`${label} ${percentage === null ? 'percentage unavailable' : `${percentage}% left`}`}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={percentage ?? undefined}
      aria-valuetext={percentage === null ? `${label} percentage unavailable` : `${percentage}% remaining`}
    >
      <svg viewBox="0 0 100 100" aria-hidden="true">
        <defs>
          <linearGradient id={gradientId} x1="12%" y1="88%" x2="88%" y2="12%">
            <stop className="ein-gauge-gradient-start" offset="0%" />
            <stop className="ein-gauge-gradient-end" offset="100%" />
          </linearGradient>
        </defs>
        <circle className="ein-gauge-track" cx="50" cy="50" r={radius} pathLength="100" />
        {percentage !== null && (
          <circle
            className="ein-gauge-value"
            cx="50"
            cy="50"
            r={radius}
            pathLength="100"
            stroke={`url(#${gradientId})`}
            strokeDasharray={`${percentage} 100`}
          />
        )}
      </svg>
      <span className="ein-gauge__core">
        <span className="ein-gauge__value">{displayValue}</span>
        <span className="ein-gauge__caption">left</span>
      </span>
    </div>
  )
}
