import { type KeyboardEvent, type PointerEvent, useCallback, useEffect, useRef, useState } from 'react'

export interface HoldBind {
  onPointerDown: (e: PointerEvent) => void
  onPointerUp: () => void
  onPointerLeave: () => void
  onPointerCancel: () => void
  onKeyDown: (e: KeyboardEvent) => void
  onKeyUp: (e: KeyboardEvent) => void
  onContextMenu: (e: { preventDefault(): void }) => void
}

/**
 * Segurar por `ms` para confirmar. Soltar antes = toque curto (`onShortTap`); sair do botão ou cancelar o
 * toque desiste sem avisar. Enter e Espaço seguram igual ao dedo. Se `enabled` ficar falso durante o
 * segurar, desiste (ex.: o aviso já saiu por outro caminho).
 */
export function useHold(
  onComplete: () => void,
  opts: { ms: number; enabled: boolean; onShortTap: () => void },
): { holding: boolean; bind: HoldBind } {
  const [holding, setHolding] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const latest = useRef({ onComplete, onShortTap: opts.onShortTap })
  latest.current = { onComplete, onShortTap: opts.onShortTap }
  const { ms, enabled } = opts

  const cancel = useCallback(() => {
    clearTimeout(timer.current)
    timer.current = undefined
    setHolding(false)
  }, [])

  const start = useCallback(() => {
    if (!enabled || timer.current !== undefined) return
    setHolding(true)
    timer.current = setTimeout(() => {
      timer.current = undefined
      setHolding(false)
      latest.current.onComplete()
    }, ms)
  }, [enabled, ms])

  const release = useCallback(() => {
    if (timer.current === undefined) return
    cancel()
    latest.current.onShortTap()
  }, [cancel])

  useEffect(() => {
    if (!enabled) cancel()
  }, [enabled, cancel])
  useEffect(() => cancel, [cancel])

  const isActivationKey = (e: KeyboardEvent) => e.key === 'Enter' || e.key === ' '

  const bind: HoldBind = {
    onPointerDown: (e) => {
      e.preventDefault()
      start()
    },
    onPointerUp: release,
    onPointerLeave: cancel,
    onPointerCancel: cancel,
    onKeyDown: (e) => {
      if (!isActivationKey(e)) return
      e.preventDefault()
      if (!e.repeat) start()
    },
    onKeyUp: (e) => {
      if (!isActivationKey(e)) return
      e.preventDefault()
      release()
    },
    onContextMenu: (e) => e.preventDefault(),
  }
  return { holding, bind }
}
