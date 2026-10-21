import { describe, expect, it } from 'vitest'
import { extractPlate, locatePlate, normalizePlate } from './plate'

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

  it.each(['abc123', 'abc12345', 'corolla', '2bc1234', 'abcx234', 'ábc1234', 'abc1234á', ''])(
    '%j não é placa',
    (candidate) => {
      expect(normalizePlate(candidate)).toBeNull()
    },
  )
})

describe('extractPlate', () => {
  it('a placa deixa uma quebra de linha no lugar dela, sem a palavra "placa"', () => {
    expect(extractPlate('gol branco placa ffo4745 farol aceso')).toEqual({
      plate: 'FFO-4745',
      rest: 'gol branco\nfarol aceso',
    })
    expect(extractPlate('hb20 vermelho abc1234 parado na garagem')?.rest).toBe('hb20 vermelho\nparado na garagem')
  })

  it('placa sozinha numa linha, nas pontas ou linhas em branco não deixam linha vazia', () => {
    expect(extractPlate('abc1234')?.rest).toBe('')
    expect(extractPlate('gol\n\nabc1234\n\nfarol')?.rest).toBe('gol\nfarol')
  })

  it('quebras que já estavam no texto ficam e os espaços de cada linha são juntados', () => {
    expect(extractPlate('  gol   branco \t\n  abc1234  ')?.rest).toBe('gol branco')
    expect(extractPlate('gol\r\nbranco abc1234')?.rest).toBe('gol\nbranco')
  })

  it('devolve null quando não há placa', () => {
    expect(extractPlate('corolla prata vidro aberto')).toBeNull()
    expect(extractPlate('')).toBeNull()
  })

  it('lê "placa:" colado, hífen e espaço no meio, entre separadores', () => {
    expect(extractPlate('creta preto | placa:gbr- 9003 / farol aceso')?.plate).toBe('GBR-9003')
    expect(extractPlate('creta preto placa-gbr9003')?.plate).toBe('GBR-9003')
  })

  it('palavras comuns que cabem nos espaços da placa não são placa (precisa de dígito)', () => {
    expect(extractPlate('motivos diversos')).toBeNull()
  })

  it('placa junta ou com hífen ganha de placa separada só por espaço, onde quer que esteja', () => {
    expect(extractPlate('abc 1234 depois xyz5678')?.plate).toBe('XYZ-5678')
    expect(extractPlate('abc1234 depois xyz5678')).toEqual({ plate: 'ABC-1234', rest: 'depois xyz5678' })
  })

  it('separada só por espaço: só se for Mercosul ou vier depois da palavra "placa"', () => {
    expect(extractPlate('gol 2015')).toBeNull()
    expect(extractPlate('abc 1234')).toBeNull()
    expect(extractPlate('QWE 1B23')?.plate).toBe('QWE-1B23')
    expect(extractPlate('placa abc 1234')?.plate).toBe('ABC-1234')
  })

  it('placa não pode estar colada em outras letras ou dígitos', () => {
    expect(extractPlate('xabc1234')).toBeNull()
    expect(extractPlate('abc12345')).toBeNull()
    expect(extractPlate('ábc1234')).toBeNull()
    expect(extractPlate('abc1234á')).toBeNull()
    expect(extractPlate('abc1234,')?.plate).toBe('ABC-1234')
  })

  it('letra matemática fora do BMP (par substituto) não conta como letra colada, como no .NET', () => {
    const pair = String.fromCodePoint(0x1d400)
    expect(extractPlate(`abc1234${pair}`)?.plate).toBe('ABC-1234')
    expect(extractPlate(`${pair}abc1234`)?.plate).toBe('ABC-1234')
  })

  it('não trava com uma fila enorme de espaços', () => {
    const started = Date.now()
    expect(extractPlate(`abc${' '.repeat(20000)}x`)).toBeNull()
    expect(Date.now() - started).toBeLessThan(2000)
  })
})

describe('locatePlate', () => {
  it('diz onde está a mesma placa, com o rótulo "placa:" dentro', () => {
    const text = 'gol branco placa: ffo4745 farol'
    const at = locatePlate(text)
    expect(at).not.toBeNull()
    expect(text.slice(at!.index, at!.index + at!.length)).toBe('placa: ffo4745')
    expect(locatePlate('gol branco')).toBeNull()
  })
})
