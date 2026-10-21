import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { LiveBar } from '../data/types'
import { useBackend } from '../state/AppData'
import { barMetrics, LED_WIDTH, tickerX } from './ticker'

const REDUCED_MOTION = '(prefers-reduced-motion: reduce)'

function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() => (typeof matchMedia === 'function' ? matchMedia(REDUCED_MOTION).matches : false))
  useEffect(() => {
    if (typeof matchMedia !== 'function') return undefined
    const query = matchMedia(REDUCED_MOTION)
    const onChange = () => setReduced(query.matches)
    onChange()
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])
  return reduced
}

/** px do LED -> cqw do mini-telão (a largura toda, 2560 px, é 100 cqw). */
const cqw = (ledPx: number): string => `${(ledPx * 100) / LED_WIDTH}cqw`

/**
 * Um espelho pequeno do telão: a barra passa o texto com a mesma posição, tamanho e velocidade de `/live/bar`,
 * começando do `startedAt` (hora do servidor) para bater com o que o LED mostra. Sem `text` não há barra.
 * Com "reduzir movimento" ligado o texto fica parado.
 */
export function MiniLed({ text, bar, startedAt }: { text: string | null; bar: LiveBar; startedAt?: number }) {
  const backend = useBackend()
  const ledRef = useRef<HTMLDivElement>(null)
  const tickRef = useRef<HTMLDivElement>(null)
  const still = usePrefersReducedMotion()
  const { height, fontSize, top } = barMetrics(bar)
  const speed = bar.speed
  const showing = text !== null

  useLayoutEffect(() => {
    const led = ledRef.current
    const tick = tickRef.current
    if (!led || !tick || !showing || still) return undefined

    // Quanto do trajeto já passou quando este efeito começa; depois soma o tempo dos quadros.
    const t0 = performance.now()
    const base = startedAt === undefined ? 0 : backend.serverNow() - startedAt
    let id = 0

    const place = (t: number) => {
      const ledPx = led.clientWidth
      const textPx = ledPx > 0 ? (tick.offsetWidth * LED_WIDTH) / ledPx : 0
      tick.style.transform = `translate(${cqw(tickerX(base + (t - t0), speed, textPx))}, -50%)`
    }
    const frame = (t: number) => {
      place(t)
      id = requestAnimationFrame(frame)
    }
    place(t0)
    id = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(id)
  }, [backend, showing, still, speed, startedAt, text])

  return (
    <div className="led" ref={ledRef} aria-hidden="true" data-testid="mini-led">
      <div className="slide" />
      {showing && (
        <div className="bar" style={{ height: cqw(height), top: top ? 0 : 'auto', bottom: top ? 'auto' : 0 }}>
          <div
            className={still ? 'tick still' : 'tick'}
            ref={tickRef}
            style={{ fontSize: cqw(fontSize) }}
          >
            {text}
          </div>
        </div>
      )}
    </div>
  )
}
