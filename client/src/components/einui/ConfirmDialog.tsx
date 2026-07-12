import { useEffect, useRef, useState, type ReactNode } from 'react'
import { AlertTriangle } from 'lucide-react'
import { createPortal } from 'react-dom'
import { GlassButton } from './Glass'

type ConfirmDialogProps = {
  title: string
  description: ReactNode
  confirmLabel: string
  onConfirm: () => void | Promise<void>
  onCancel: () => void
}

export function ConfirmDialog({ title, description, confirmLabel, onConfirm, onCancel }: ConfirmDialogProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const cancelRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    cancelRef.current?.focus()
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onCancel()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [busy, onCancel])

  const confirm = async () => {
    if (busy) return
    setError('')
    setBusy(true)
    try {
      await onConfirm()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Request failed.')
    } finally {
      setBusy(false)
    }
  }

  return createPortal(
    <div className="confirm-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget && !busy) onCancel()
    }}>
      <div className="confirm-dialog" role="alertdialog" aria-modal="true" aria-labelledby="confirm-dialog-title" aria-describedby="confirm-dialog-description">
        <div className="confirm-dialog-icon" aria-hidden="true"><AlertTriangle size={20} /></div>
        <div className="confirm-dialog-content">
          <h2 id="confirm-dialog-title">{title}</h2>
          <div id="confirm-dialog-description" className="muted">{description}</div>
          {error && <div className="error-text">{error}</div>}
          <div className="button-row confirm-dialog-actions">
            <GlassButton ref={cancelRef} variant="ghost" type="button" onClick={onCancel} disabled={busy}>Cancel</GlassButton>
            <GlassButton variant="danger" type="button" onClick={() => void confirm()} disabled={busy}>
              {busy ? 'Working…' : confirmLabel}
            </GlassButton>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  )
}
