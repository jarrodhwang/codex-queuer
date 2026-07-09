import { useEffect, useMemo, useRef, useState } from 'react'
import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from 'react'
import { Check, ChevronDown } from 'lucide-react'
import { cn } from '@/lib/utils'

export function GlassPanel({ className, children }: { className?: string; children: ReactNode }) {
  return <section className={cn('glass-panel', className)}>{children}</section>
}

export function GlassButton({
  className,
  variant = 'secondary',
  size = 'md',
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger'
  size?: 'sm' | 'md' | 'icon'
}) {
  return <button className={cn('glass-button', `glass-button--${variant}`, `glass-button--${size}`, className)} {...props} />
}

export function GlassInput({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return <input className={cn('glass-field', className)} {...props} />
}

export function GlassTextarea({ className, ...props }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={cn('glass-field glass-textarea', className)} {...props} />
}

export function GlassSelect({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className={cn('glass-field', className)} {...props} />
}

export type GlassDropdownOption = {
  label: string
  value: string
}

export function GlassDropdownSelect({
  label,
  options,
  value,
  disabled = false,
  className,
  onChange,
}: {
  label: string
  options: GlassDropdownOption[]
  value: string
  disabled?: boolean
  className?: string
  onChange: (value: string) => void
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement | null>(null)
  const selected = useMemo(() => options.find((option) => option.value === value) ?? options[0], [options, value])
  const selectedIndex = Math.max(0, options.findIndex((option) => option.value === value))

  useEffect(() => {
    if (!open) {
      return
    }

    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) {
        setOpen(false)
      }
    }

    document.addEventListener('pointerdown', closeOnOutsidePointer)
    return () => document.removeEventListener('pointerdown', closeOnOutsidePointer)
  }, [open])

  const selectOption = (nextValue: string) => {
    onChange(nextValue)
    setOpen(false)
  }

  return (
    <div ref={rootRef} className={cn('glass-select', open && 'open', className)}>
      <button
        type="button"
        className="glass-select-button"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={label}
        onClick={() => setOpen((current) => !current)}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            const direction = event.key === 'ArrowDown' ? 1 : -1
            const nextIndex = (selectedIndex + direction + options.length) % options.length
            onChange(options[nextIndex].value)
            setOpen(true)
          }

          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            setOpen((current) => !current)
          }

          if (event.key === 'Escape') {
            setOpen(false)
          }
        }}
      >
        <span className="glass-select-value truncate">{selected?.label ?? 'Select'}</span>
        <span className="glass-select-arrow" aria-hidden="true">
          <ChevronDown size={16} />
        </span>
      </button>
      {open && (
        <div className="glass-select-menu" role="listbox" aria-label={`${label} options`}>
          {options.map((option) => {
            const active = option.value === value
            return (
              <button
                key={option.value}
                type="button"
                className={cn('glass-select-option', active && 'active')}
                role="option"
                aria-selected={active}
                onClick={() => selectOption(option.value)}
              >
                <span className="glass-select-option-label truncate">{option.label}</span>
                {active && <Check size={14} />}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}

export function FieldLabel({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field-label">
      <span>{label}</span>
      {children}
    </label>
  )
}
