import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from 'react'
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

export function FieldLabel({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field-label">
      <span>{label}</span>
      {children}
    </label>
  )
}
