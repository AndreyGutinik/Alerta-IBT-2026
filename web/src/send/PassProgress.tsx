import { useEffect, useState } from 'react'
import type { LiveBar, LiveNow } from '../data/types'
import { useBackend } from '../state/AppData'
import { barMetrics } from '../status/ticker'
import { passProgress } from './passMath'

/** De quanto em quanto tempo a linha anda (a transição CSS suaviza entre um passo e outro). */
const TICK_MS = 250

let canvas: HTMLCanvasElement | undefined

/**
 * Largura, em px do LED, que o texto ocupa na barra (fonte Marble no tamanho da barra). Sem canvas (testes, navegador
 * antigo) estima pelo número de letras: basta para a linha de progresso, que não precisa ser exata.
 */
export function measureTextPx(text: string, fontSize: number): number {
  const estimate = text.length * fontSize * 0.62
  if (typeof document === 'undefined' || /jsdom/i.test(navigator.userAgent)) return estimate
  try {
    canvas ??= document.createElement('canvas')
    const ctx = canvas.getContext('2d')
    if (!ctx) return estimate
    ctx.font = `600 ${fontSize}px Marble, sans-serif`
    const width = ctx.measureText(text).width
    return Number.isFinite(width) && width > 0 ? width : estimate
  } catch {
    return estimate
  }
}

/**
 * A linha fina embaixo do aviso "passando": quanto da passagem no LED já foi, igual à linha de progresso do painel
 * do PC. Conta pelo `startedAt` do PC (hora do servidor) e pela posição/velocidade da barra de `/live`.
 */
export function PassProgress({ now, bar }: { now: LiveNow; bar: LiveBar }) {
  const backend = useBackend()
  const { fontSize } = barMetrics(bar)
  const [textPx] = useState(() => measureTextPx(now.text, fontSize))
  const [clock, setClock] = useState(() => backend.serverNow())

  useEffect(() => {
    setClock(backend.serverNow())
    const id = setInterval(() => setClock(backend.serverNow()), TICK_MS)
    return () => clearInterval(id)
  }, [backend, now.id])

  const value = passProgress(clock - now.startedAt, bar.speed, textPx)
  return (
    <span
      className="pg"
      role="progressbar"
      aria-label="Andamento na barra"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(value * 100)}
    >
      <i style={{ transform: `scaleX(${value})` }} />
    </span>
  )
}
