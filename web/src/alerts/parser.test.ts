import { describe, expect, it } from 'vitest'
import { MAX_RAW, cleanRaw, fieldValue, formatFree, upper } from './format'
import { parseAlert } from './parser'
import { alertText, decide, isTooLong, missingFields } from './rules'
import { fold, isColor, isColorModifier, isReasonStart } from './vocabulary'

describe('fieldValue (campo, a cada tecla)', () => {
  it('troca quebras de linha por espaço', () => {
    expect(fieldValue('a\nb\r\nc')).toBe('a b c')
  })

  it('corta em 300 caracteres', () => {
    expect(fieldValue('x'.repeat(400))).toHaveLength(MAX_RAW)
  })

  it('não apara, porque roda a cada tecla', () => {
    expect(fieldValue('gol ')).toBe('gol ')
  })

  it('não corta um emoji ao meio', () => {
    expect(fieldValue('x'.repeat(299) + '🚗y')).toBe('x'.repeat(299))
    expect(fieldValue('x'.repeat(298) + '🚗y')).toBe('x'.repeat(298) + '🚗')
  })
})

describe('cleanRaw (texto que vai no pedido)', () => {
  it('junta espaços e apara, mantendo o #', () => {
    expect(cleanRaw('  #gol   branco \n ffo4745 ')).toBe('#gol branco ffo4745')
  })

  it('não deixa espaço no fim depois de cortar', () => {
    expect(cleanRaw('aviso '.repeat(67))).toBe('aviso '.repeat(50).trim())
  })
})

describe('upper', () => {
  it('junta espaços, apara e põe em maiúsculas com acento', () => {
    expect(upper('  ônix   à vista \n')).toBe('ÔNIX À VISTA')
  })
})

describe('aviso livre do admin com mais de 300 caracteres', () => {
  const raw = 'aviso '.repeat(67)

  it('continua aviso livre, sem espaço duplo, igual ao texto em maiúsculas', () => {
    expect(raw.length).toBeGreaterThan(MAX_RAW)

    const alert = parseAlert(raw)
    const text = formatFree(alert.freeText)

    expect(decide(alert, 'admin').kind).toBe('free')
    expect(text).not.toMatch(/\s{2}/)
    expect(text).toBe(upper(raw))
  })
})


describe('parseAlert', () => {
  it('o primeiro segmento que não começa com motivo é o veículo', () => {
    expect(parseAlert('farol aceso | gol branco | ffo4745')).toMatchObject({
      model: 'GOL',
      color: 'BRANCO',
      plate: 'FFO-4745',
      reason: 'FAROL ACESO',
    })
  })

  it('a placa separa o veículo do motivo mesmo sem palavra de motivo', () => {
    expect(parseAlert('corolla prata abc1234 bateu no poste')).toMatchObject({
      model: 'COROLLA',
      color: 'PRATA',
      reason: 'BATEU NO POSTE',
    })
  })

  it('vários motivos são juntados com vírgula', () => {
    expect(parseAlert('gol branco farol aceso | vidro aberto / porta aberta').reason).toBe(
      'FAROL ACESO, VIDRO ABERTO, PORTA ABERTA',
    )
  })

  it('palavra-gatilho dentro do veículo abre o motivo; o que vem antes fica no veículo', () => {
    expect(parseAlert('gol branco farol aceso')).toMatchObject({ model: 'GOL', color: 'BRANCO', reason: 'FAROL ACESO' })
  })

  it('cor com modificador leva o modificador junto; o resto, na ordem digitada, é o modelo', () => {
    expect(parseAlert('azul marinho onix abc1234')).toMatchObject({ model: 'ONIX', color: 'AZUL MARINHO' })
    expect(parseAlert('onix cinza metálico abc1234')).toMatchObject({ model: 'ONIX', color: 'CINZA METÁLICO' })
  })

  it('sem cor no veículo, a cor pode abrir o segmento logo depois dele', () => {
    expect(parseAlert('gol abc1234 branco farol aceso')).toMatchObject({
      model: 'GOL',
      color: 'BRANCO',
      reason: 'FAROL ACESO',
    })
  })

  it('reconhece cor e motivo sem diferenciar maiúsculas nem acento', () => {
    expect(parseAlert('Gol PRATA faróis acesos')).toMatchObject({
      model: 'GOL',
      color: 'PRATA',
      plate: null,
      reason: 'FARÓIS ACESOS',
    })
  })

  it('pontuação colada na palavra não esconde a cor nem o motivo e não entra no modelo', () => {
    expect(parseAlert('gol, branco. (farol aceso)')).toMatchObject({ model: 'GOL', color: 'BRANCO', reason: 'FAROL ACESO' })
    expect(parseAlert('(onix) preto! abc1234')).toMatchObject({ model: 'ONIX', color: 'PRETO' })
    expect(parseAlert('-onix- preto abc1234').model).toBe('ONIX')
  })

  it('vírgula entre dois dígitos é número e não separa; as outras vírgulas separam', () => {
    expect(parseAlert('gol branco abc1234 motor 1,0 turbo').reason).toBe('MOTOR 1,0 TURBO')
    expect(parseAlert('gol branco abc1234 motor 1, 0 turbo').reason).toBe('MOTOR 1, 0 TURBO')
    expect(parseAlert('gol branco abc1234 farol aceso, vidro aberto').reason).toBe('FAROL ACESO, VIDRO ABERTO')
  })

  it('a palavra "placa" sozinha nunca vira modelo', () => {
    expect(parseAlert('gol placa branco abc1234').model).toBe('GOL')
  })

  it('palavra solta sem letra nem dígito (um hífen) some', () => {
    expect(parseAlert('gol - branco abc1234')).toMatchObject({ model: 'GOL', color: 'BRANCO' })
  })

  it('emoji, moldura de tecla e o # do começo saem; nada é cortado', () => {
    const alert = parseAlert('#gol branco\nffo4745\n\nfarol aceso \u{1F697}')
    expect(alert.freeText).toBe('GOL BRANCO FFO4745 FAROL ACESO')
    expect(alert.reason).toBe('FAROL ACESO')
    expect(parseAlert('gol 1\u{FE0F}\u{20E3} branco abc1234').model).toBe('GOL 1')
    expect(parseAlert('gol branco abc1234 \u{1F44D}\u{1F3FD}').reason).toBe('')
    expect(parseAlert('\u{1F697}\u{1F697}').freeText).toBe('')
  })

  it('tira o # do começo, mesmo repetido, mas só o do começo', () => {
    expect(parseAlert('\n##gol branco ffo4745')).toMatchObject({ model: 'GOL', freeText: 'GOL BRANCO FFO4745' })
    expect(parseAlert('gol # branco').freeText).toBe('GOL # BRANCO')
  })

  it('o texto livre fica numa linha só, em maiúsculas, com a pontuação como veio', () => {
    expect(parseAlert('#reunião hoje |  às 19h\nna sala 2 ').freeText).toBe('REUNIÃO HOJE | ÀS 19H NA SALA 2')
  })

  it('texto vazio ou só # e emoji não tem nada', () => {
    expect(parseAlert('')).toEqual({ model: '', color: '', plate: null, reason: '', freeText: '' })
    expect(parseAlert('#\u{1F697}').freeText).toBe('')
  })
})

describe('regras do aviso', () => {
  it('nada para mostrar é recusado para todo mundo, até admin e operador', () => {
    for (const role of ['member', 'admin', 'operator'] as const) {
      expect(decide(parseAlert('#\u{1F697}'), role).kind).toBe('reject')
    }
  })

  it('operador tem as mesmas regras do admin', () => {
    expect(decide(parseAlert('gol branco farol aceso'), 'operator')).toEqual({ kind: 'free', missing: ['placa'] })
    expect(decide(parseAlert('corolla abc1234'), 'operator')).toEqual({ kind: 'vehicle', missing: ['cor'] })
  })

  it('texto de aviso: recusa não tem texto; livre não tem estrutura', () => {
    const rejected = parseAlert('gol branco')
    expect(alertText(rejected, decide(rejected, 'member'))).toBe('')
    expect(alertText(rejected, decide(rejected, 'admin'))).toBe('GOL BRANCO')
  })

  it('isTooLong: membro passa de 200 caracteres do texto formatado; admin e operador não têm limite', () => {
    expect(isTooLong('x'.repeat(200), 'member')).toBe(false)
    expect(isTooLong('x'.repeat(201), 'member')).toBe(true)
    expect(isTooLong('x'.repeat(5000), 'admin')).toBe(false)
    expect(isTooLong('x'.repeat(5000), 'operator')).toBe(false)
  })
})

describe('vocabulário', () => {
  it('cor: minúsculas, sem acento e sem pontuação nas pontas', () => {
    expect(isColor('Prata')).toBe(true)
    expect(isColor('CINZA')).toBe(true)
    expect(isColor('(branco.')).toBe(true)
    expect(isColor('corolla')).toBe(false)
    expect(isColorModifier('Metálico,')).toBe(true)
  })

  it('início de motivo: lista e prefixos, minúsculas e sem acento', () => {
    for (const word of ['farol', 'Faróis', 'estacionado', 'parada', 'comparecer', 'bloqueando', 'som', 'sons', '(Aceso']) {
      expect(isReasonStart(word), word).toBe(true)
    }
    for (const word of ['gol', 'para', 'branco', 'somente', 'sombra', 'compareça']) {
      expect(isReasonStart(word), word).toBe(false)
    }
  })

  it('fold: minúsculas, sem acento e sem pontuação nas pontas', () => {
    expect(fold('(Metálico.')).toBe('metalico')
    expect(fold('ÔNIX')).toBe('onix')
  })
})

describe('missingFields', () => {
  it('lista modelo, cor e placa nessa ordem', () => {
    expect(missingFields(parseAlert(''))).toEqual(['modelo', 'cor', 'placa'])
  })

  it('campo só com espaços conta como faltando', () => {
    const alert = { model: ' ', color: 'prata', plate: 'ABC-1234', reason: '', freeText: '' }

    expect(missingFields(alert)).toEqual(['modelo'])
  })
})
