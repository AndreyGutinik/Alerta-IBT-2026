import { describe, expect, it } from 'vitest'
import { parseAlert } from '../alerts/parser'
import { colorAllowed, editDistance, foldedWords, guardAiResult, hasPlateContiguously, isFaithful, readAiAlert } from './guard'
import type { AiAlert } from './types'

function ai(partial: Partial<AiAlert> = {}): AiAlert {
  return { kind: 'vehicle', modelo: '', cor: '', placa: '', motivo: '', texto: '', ...partial }
}

function guard(raw: string, answer: AiAlert | null) {
  return guardAiResult(raw, answer, parseAlert(raw))
}

describe('editDistance', () => {
  it('conta inserção, remoção e troca', () => {
    expect(editDistance('recepssao', 'recepcao')).toBe(2)
    expect(editDistance('', 'abc')).toBe(3)
    expect(editDistance('abc', '')).toBe(3)
    expect(editDistance('gol', 'gol')).toBe(0)
    expect(editDistance('kitten', 'sitting')).toBe(3)
  })
})

describe('guardAiResult: IA boa', () => {
  it('corrige a grafia: gemini, com os campos da IA e a placa de exibição', () => {
    const result = guard(
      'prata corolla bra2e19 vidro abrto',
      ai({ modelo: 'Corolla', cor: 'prata', placa: 'BRA2E19', motivo: 'vidro aberto' }),
    )

    expect(result.by).toBe('gemini')
    expect(result.refused).toBe(false)
    expect(result.alert).toMatchObject({ model: 'Corolla', color: 'prata', plate: 'BRA-2E19', reason: 'vidro aberto' })
  })

  it('sem texto na resposta, o texto livre junta os campos', () => {
    const result = guard('gol branco ffo4745', ai({ modelo: 'Gol', cor: 'branco', placa: 'FFO4745' }))

    expect(result.alert.freeText).toBe('Gol branco FFO4745')
  })

  it('aviso livre: a IA corrige o texto e a trava deixa passar', () => {
    const text = 'Pais do Pedro Henrique, da sala kids, favor comparecer à recepção'
    const result = guard(
      'pais do pedro henrique da sala kids favor comparecer na recepssao',
      ai({ kind: 'free', texto: text }),
    )

    expect(result.by).toBe('gemini')
    expect(result.alert).toEqual({ model: '', color: '', plate: null, reason: '', freeText: text })
  })

  it('placa digitada com espaço: vale se a IA leu os 7 caracteres em sequência', () => {
    for (const raw of ['gol branco abc 1234 farol aceso', 'gol branco ab c1234 farol aceso', 'gol branco a b c 1 2 3 4 farol aceso']) {
      const result = guard(raw, ai({ modelo: 'Gol', cor: 'branco', placa: 'ABC1234', motivo: 'farol aceso' }))

      expect(result.by, raw).toBe('gemini')
      expect(result.alert.plate).toBe('ABC-1234')
    }
  })

  it('cor feminina e masculina são a mesma; modificador que o texto tem passa', () => {
    expect(guard('gol preta abc1234', ai({ modelo: 'Gol', cor: 'Preto', placa: 'ABC1234' })).by).toBe('gemini')
    expect(guard('gol branco abc1234', ai({ modelo: 'Gol', cor: 'Branca', placa: 'ABC1234' })).by).toBe('gemini')
    expect(guard('gol azul marinho abc1234', ai({ modelo: 'Gol', cor: 'azul marinho', placa: 'ABC1234' })).by).toBe('gemini')
  })

  it('palavras novas curtas e artigos são aceitos como correção', () => {
    const result = guard('onix preto abc1234 parado na garagem', ai({ modelo: 'Ônix', cor: 'preto', placa: 'ABC1234', motivo: 'parado à garagem' }))

    expect(result.by).toBe('gemini')
  })
})

describe('guardAiResult: a trava', () => {
  it('placa diferente da do texto vai para o local', () => {
    const result = guard('gol branco abc1234', ai({ modelo: 'Gol', cor: 'branco', placa: 'ABC9999' }))

    expect(result.by).toBe('local')
    expect(result.alert.plate).toBe('ABC-1234')
  })

  it('IA sem placa quando o texto tem, ou com placa quando o texto não tem, vai para o local', () => {
    expect(guard('gol branco abc1234', ai({ modelo: 'Gol', cor: 'branco' })).by).toBe('local')
    expect(guard('gol branco farol aceso', ai({ modelo: 'Gol', cor: 'branco', placa: 'ABC1234' })).by).toBe('local')
  })

  it('placa inventada com o rabo de uma palavra ou no meio de duas palavras não vale', () => {
    expect(guard('gol branco farol aceso 1234', ai({ modelo: 'Gol', cor: 'branco', placa: 'ESO1234' })).by).toBe('local')
    expect(guard('gol branco farol 1234 no estacionamento', ai({ modelo: 'Gol', cor: 'branco', placa: 'ROL1234' })).by).toBe('local')
  })

  it('placa escrita dentro de outro campo também tem de ser a do original', () => {
    const result = guard('gol branco abc1234', ai({ modelo: 'Gol', cor: 'branco', placa: 'ABC1234', texto: 'Gol branco XYZ9876' }))

    expect(result.by).toBe('local')
  })

  it('palavra inventada vai para o local', () => {
    const result = guard('gol branco abc1234 farol aceso', ai({ modelo: 'Corolla', cor: 'branco', placa: 'ABC1234', motivo: 'farol aceso' }))

    expect(result.by).toBe('local')
  })

  it('palavra curta só aceita 1 edição ("fox" não é erro de "gol")', () => {
    expect(guard('gol branco abc1234', ai({ modelo: 'Fox', cor: 'branco', placa: 'ABC1234' })).by).toBe('local')
    expect(guard('gol branco abc1234', ai({ modelo: 'Gal', cor: 'branco', placa: 'ABC1234' })).by).toBe('gemini')
  })

  it('negação nova, número novo e palavra com dígito diferente não passam', () => {
    expect(guard('gol branco abc1234 vidro aberto', ai({ modelo: 'Gol', cor: 'branco', placa: 'ABC1234', motivo: 'vidro não aberto' })).by).toBe('local')
    expect(guard('hb20 branco abc1234', ai({ modelo: 'HB30', cor: 'branco', placa: 'ABC1234' })).by).toBe('local')
  })

  it('cor trocada, tirada do aviso de veículo ou com modificador que o texto não tem vai para o local', () => {
    expect(guard('gol branco abc1234', ai({ modelo: 'Gol', cor: 'prata', placa: 'ABC1234' })).by).toBe('local')
    expect(guard('gol branco abc1234', ai({ modelo: 'Gol', cor: '', placa: 'ABC1234' })).by).toBe('local')
    expect(guard('gol branco abc1234', ai({ modelo: 'Gol', cor: 'branco claro', placa: 'ABC1234' })).by).toBe('local')
    expect(guard('gol branco claro abc1234', ai({ modelo: 'Gol', cor: 'branco claro', placa: 'ABC1234' })).by).toBe('gemini')
  })

  it('aviso livre da IA sem cor, para texto que cita cor, não é barrado pela cor', () => {
    const result = guard('o carro branco da pastora ficou aberto', ai({ kind: 'free', texto: 'O carro branco da pastora ficou aberto' }))

    expect(result.by).toBe('gemini')
  })

  it('aviso livre da IA para texto com placa vai para o local (a placa some do aviso livre)', () => {
    expect(guard('gol branco abc1234', ai({ kind: 'free', texto: 'Gol branco abc1234' })).by).toBe('local')
  })

  it('texto com dois cores aceita qualquer uma das duas, não uma terceira', () => {
    expect(guard('gol branco e azul abc1234', ai({ modelo: 'Gol', cor: 'azul', placa: 'ABC1234' })).by).toBe('gemini')
    expect(guard('gol branco e azul abc1234', ai({ modelo: 'Gol', cor: 'verde', placa: 'ABC1234' })).by).toBe('local')
  })

  it('texto sem cor: qualquer cor da IA passa pela trava de cor, mas a palavra nova não', () => {
    expect(guard('gol abc1234', ai({ modelo: 'Gol', cor: '', placa: 'ABC1234' })).by).toBe('gemini')
    expect(guard('gol abc1234', ai({ modelo: 'Gol', cor: 'prata', placa: 'ABC1234' })).by).toBe('local')
  })
})

describe('guardAiResult: recusa e IA que não deu', () => {
  it('free com tudo vazio é recusa: refused, gemini, sem cair no local', () => {
    const result = guard('texto qualquer', ai({ kind: 'free' }))

    expect(result).toEqual({ alert: { model: '', color: '', plate: null, reason: '', freeText: '' }, by: 'gemini', refused: true })
  })

  it('a recusa vale mesmo quando o texto tem placa (vem antes da trava)', () => {
    expect(guard('gol branco abc1234', ai({ kind: 'free' })).refused).toBe(true)
  })

  it('IA null, tipo desconhecido ou vehicle sem nada lido vão para o local, sem recusa', () => {
    const local = parseAlert('gol branco abc1234')

    expect(guardAiResult('gol branco abc1234', null, local)).toEqual({ alert: local, by: 'local', refused: false })
    expect(guard('gol branco abc1234', ai({ kind: 'other' as 'free' })).by).toBe('local')
    expect(guard('gol branco abc1234', ai({ kind: 'vehicle' })).by).toBe('local')
    expect(guard('gol branco abc1234', ai({ kind: 'vehicle' })).refused).toBe(false)
  })
})

describe('peças da trava', () => {
  it('readAiAlert: free sem texto é recusa; vehicle com placa que não é placa fica sem placa', () => {
    expect(readAiAlert(ai({ kind: 'free', texto: 'Aviso' }))).toEqual({ model: '', color: '', plate: null, reason: '', freeText: 'Aviso' })
    expect(readAiAlert(ai({ kind: 'vehicle', modelo: 'Gol', placa: 'XYZ' }))?.plate).toBeNull()
    expect(readAiAlert(ai({ kind: 'vehicle', modelo: 'Gol', placa: 'xyz-1d23' }))?.plate).toBe('XYZ-1D23')
  })

  it('hasPlateContiguously: os 7 começam numa palavra', () => {
    expect(hasPlateContiguously('gol abc 1234', 'ABC-1234')).toBe(true)
    expect(hasPlateContiguously('aceso 1234', 'ESO-1234')).toBe(false)
    expect(hasPlateContiguously('a-b-c 1234', 'ABC-1234')).toBe(true)
  })

  it('foldedWords: sem acento, sem pontuação nas pontas, vírgula entre dígitos fica, "placa" some', () => {
    expect(foldedWords('Óleo, motor 1,0 (turbo). PLACA: x/y;z')).toEqual(['oleo', 'motor', '1,0', 'turbo', 'x', 'y', 'z'])
    expect(foldedWords('pé-de-moleque a-1')).toEqual(['pe', 'de', 'moleque', 'a-1'])
  })

  it('isFaithful: igual, artigo, até 2 edições (1 nas curtas); dígito e negação não', () => {
    const raw = new Set(['recepssao', 'gol', 'hb20', 'vidro'])

    expect(isFaithful('recepcao', raw)).toBe(true)
    expect(isFaithful('vidros', raw)).toBe(true)
    expect(isFaithful('na', raw)).toBe(true)
    expect(isFaithful('fox', raw)).toBe(false)
    expect(isFaithful('hb30', raw)).toBe(false)
    expect(isFaithful('nao', raw)).toBe(false)
  })

  it('colorAllowed: sem cor no texto passa tudo', () => {
    expect(colorAllowed(new Set(), 'qualquer', true)).toBe(true)
    expect(colorAllowed(new Set(['preto']), 'preta', true)).toBe(true)
    expect(colorAllowed(new Set(['preto']), '', true)).toBe(false)
    expect(colorAllowed(new Set(['preto']), '', false)).toBe(true)
    expect(colorAllowed(new Set(['preto']), 'branco', false)).toBe(false)
  })
})
