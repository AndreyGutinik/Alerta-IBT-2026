import { describe, expect, it } from 'vitest'
import { barMetrics, LED_WIDTH, tickerX } from './ticker'

describe('tickerX', () => {
  it('começa na borda direita e anda speed px por segundo', () => {
    expect(tickerX(0, 90, 1000)).toBe(2560)
    expect(tickerX(10_000, 90, 1000)).toBe(1660)
  })

  it('depois de sair inteiro pela esquerda volta para a direita (laço)', () => {
    // 3560 px de percurso (2560 + 1000) a 90 px/s
    const loopMs = (3560 / 90) * 1000
    expect(tickerX(loopMs, 90, 1000)).toBeCloseTo(2560, 6)
    expect(tickerX(loopMs + 10_000, 90, 1000)).toBeCloseTo(1660, 6)
  })

  it('o texto fica fora da tela no ponto de saída (começo = -largura)', () => {
    const almost = ((3560 - 0.9) / 90) * 1000
    expect(tickerX(almost, 90, 1000)).toBeCloseTo(-999.1, 3)
  })

  it('tempo negativo (relógio adiantado) fica na entrada; velocidade 0 não anda', () => {
    expect(tickerX(-5000, 90, 1000)).toBe(LED_WIDTH)
    expect(tickerX(5000, 0, 1000)).toBe(LED_WIDTH)
  })

  it('largura da tela customizada', () => {
    expect(tickerX(1000, 100, 200, 1000)).toBe(900)
  })
})

describe('barMetrics', () => {
  it('fontSize ausente = 64% da altura, arredondado', () => {
    expect(barMetrics({ position: 'top', height: 56, speed: 90 })).toEqual({ height: 56, fontSize: 36, top: true })
  })
  it('fontSize explícito vale; bottom não é topo', () => {
    expect(barMetrics({ position: 'top', height: 56, fontSize: 40, speed: 90 }).fontSize).toBe(40)
    expect(barMetrics({ position: 'bottom', height: 56, speed: 90 }).top).toBe(false)
  })
})
