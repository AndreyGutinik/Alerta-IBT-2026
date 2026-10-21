import { Info } from 'lucide-react'
import { useEffect, useId, useRef, useState } from 'react'

/** O (i) com ajuda num balão que abre por toque ou teclado (não depende de passar o mouse). */
export function InfoPopover({ lines, label = 'Ajuda' }: { lines: string[]; label?: string }) {
  const [open, setOpen] = useState(false)
  const wrap = useRef<HTMLSpanElement>(null)
  const id = useId()

  useEffect(() => {
    if (!open) return undefined
    const onDown = (e: PointerEvent) => {
      if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
    <span className="info-wrap" ref={wrap}>
      <button
        type="button"
        className="info"
        aria-label={label}
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        onClick={() => setOpen((v) => !v)}
      >
        <Info aria-hidden="true" />
      </button>
      {open && (
        <div className="tip" id={id} role="note">
          {lines.map((line) => (
            <p key={line}>{line}</p>
          ))}
        </div>
      )}
    </span>
  )
}
