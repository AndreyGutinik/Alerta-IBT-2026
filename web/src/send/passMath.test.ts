import { describe, expect, it } from 'vitest'
import { isExactDuration, nowProgress, passProgress, passTotalMs } from './passMath'

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

describe('isExactDuration', () => {
  it('só aceita número finito maior que zero', () => {
    expect(isExactDuration(41_000)).toBe(true)
    for (const bad of [0, -1, Number.NaN, Number.POSITIVE_INFINITY, '41000', null, undefined]) {
      expect(isExactDuration(bad)).toBe(false)
    }
  })
})

describe('nowProgress', () => {
  const never = () => {
    throw new Error('não devia medir o texto')
  }

  it('com a duração exata do PC usa elapsed / duração e não mede o texto', () => {
    expect(nowProgress(0, 20_000, SPEED, never)).toBe(0)
    expect(nowProgress(5_000, 20_000, SPEED, never)).toBeCloseTo(0.25, 9)
    expect(nowProgress(20_000, 20_000, SPEED, never)).toBe(1)
  })

  it('a duração exata ganha da estimativa mesmo quando elas discordam', () => {
    expect(nowProgress(TOTAL / 2, 10_000, SPEED, () => TEXT)).toBe(1)
  })

  it('passa de 1 ou antes do início: fica preso entre 0 e 1', () => {
    expect(nowProgress(99_000, 20_000, SPEED, never)).toBe(1)
    expect(nowProgress(-3_000, 20_000, SPEED, never)).toBe(0)
  })

  it('sem duração (PC antigo) volta para a estimativa por velocidade e largura do texto', () => {
    expect(nowProgress(TOTAL / 2, undefined, SPEED, () => TEXT)).toBeCloseTo(0.5, 6)
  })

  it('duração inválida (0, negativa, NaN) também volta para a estimativa', () => {
    for (const bad of [0, -5, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(nowProgress(TOTAL / 2, bad, SPEED, () => TEXT)).toBeCloseTo(0.5, 6)
    }
  })
})
