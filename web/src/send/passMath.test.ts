import { describe, expect, it } from 'vitest'
import { passProgress, passTotalMs } from './passMath'

// Telão de 2560 px, texto de 940 px, 90 px/s: a rolagem leva (2560 + 940) / 90 = 38,888… s, mais 2 x 320 ms de entrada e saída.
const TEXT = 940
const SPEED = 90
const TOTAL = ((2560 + TEXT) / SPEED) * 1000 + 640

describe('passTotalMs', () => {
  it('entrada + rolagem (tela + texto) + saída, como a BarTimeline do PC', () => {
    expect(passTotalMs(SPEED, TEXT)).toBeCloseTo(TOTAL, 6)
  })

  it('velocidade zero ou inválida não explode', () => {
    expect(Number.isFinite(passTotalMs(0, TEXT))).toBe(true)
    expect(Number.isFinite(passTotalMs(-5, TEXT))).toBe(true)
    expect(passTotalMs(0, TEXT)).toBeGreaterThan(0)
  })
})

describe('passProgress', () => {
  it('começa em 0 e termina em 1', () => {
    expect(passProgress(0, SPEED, TEXT)).toBe(0)
    expect(passProgress(TOTAL, SPEED, TEXT)).toBe(1)
  })

  it('no meio do trajeto fica perto de 0,5', () => {
    expect(passProgress(TOTAL / 2, SPEED, TEXT)).toBeCloseTo(0.5, 6)
  })

  it('antes do início (relógio adiantado) fica em 0 e depois do fim fica em 1', () => {
    expect(passProgress(-5_000, SPEED, TEXT)).toBe(0)
    expect(passProgress(TOTAL * 3, SPEED, TEXT)).toBe(1)
  })

  it('texto maior demora mais: o mesmo tempo anda menos', () => {
    expect(passProgress(10_000, SPEED, 3000)).toBeLessThan(passProgress(10_000, SPEED, 500))
  })

  it('velocidade maior anda mais no mesmo tempo', () => {
    expect(passProgress(10_000, 180, TEXT)).toBeGreaterThan(passProgress(10_000, 90, TEXT))
  })
})
