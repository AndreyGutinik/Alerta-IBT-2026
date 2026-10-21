import { describe, expect, it } from 'vitest'
import { extractPlate, normalizePlate } from './plate'

describe('normalizePlate', () => {
  it.each([
    ['abc1234', 'ABC-1234'],
    ['abc-1d23', 'ABC-1D23'],
    ['QWE 1B23', 'QWE-1B23'],
    ['gbr- 9003', 'GBR-9003'],
    ['a8c1234', 'ABC-1234'],
    ['abcI234', 'ABC-1234'],
    ['abc-1o23', 'ABC-1O23'],
  ])('%j vira %j', (candidate, plate) => {
    expect(normalizePlate(candidate)).toBe(plate)
  })

  it.each(['abc123', 'abc12345', 'corolla', '2bc1234', 'abcx234'])('%j não é placa', (candidate) => {
    expect(normalizePlate(candidate)).toBeNull()
  })
})

describe('extractPlate', () => {
  it('acha a placa e devolve o resto sem ela e sem a palavra "placa"', () => {
    expect(extractPlate('gol branco placa ffo4745 farol aceso')).toEqual({
      plate: 'FFO-4745',
      rest: 'gol branco farol aceso',
    })
  })

  it('devolve null quando não há placa', () => {
    expect(extractPlate('corolla prata vidro aberto')).toBeNull()
  })

  it('lê "placa:" colado, hífen e espaço no meio, entre separadores', () => {
    expect(extractPlate('creta preto | placa:gbr- 9003 / farol aceso')?.plate).toBe('GBR-9003')
  })

  it('fica com a primeira placa quando há duas', () => {
    expect(extractPlate('abc1234 depois xyz5678')).toEqual({ plate: 'ABC-1234', rest: 'depois xyz5678' })
  })

  it('acha a placa logo depois do # do começo', () => {
    expect(extractPlate('#abc1234 corolla prata')?.plate).toBe('ABC-1234')
  })
})
