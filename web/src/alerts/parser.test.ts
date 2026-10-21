import { describe, expect, it } from 'vitest'
import { MAX_RAW, cleanRaw, fieldValue, formatFree, upper } from './format'
import { parseAlert } from './parser'
import { decide, missingFields } from './rules'
import { isColor, isReasonStart } from './vocabulary'

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
      model: 'gol',
      color: 'branco',
      plate: 'FFO-4745',
      reason: 'farol aceso',
    })
  })

  it('a placa separa o motivo mesmo sem palavra de motivo', () => {
    expect(parseAlert('corolla prata abc1234 bateu no poste')).toMatchObject({
      model: 'corolla',
      color: 'prata',
      reason: 'bateu no poste',
    })
  })

  it('junta vários trechos de motivo com espaço', () => {
    expect(parseAlert('gol branco farol aceso | vidro aberto / porta aberta').reason).toBe(
      'farol aceso vidro aberto porta aberta',
    )
  })

  it('reconhece cor e motivo sem diferenciar maiúsculas nem acento', () => {
    expect(parseAlert('Gol PRATA faróis acesos')).toMatchObject({
      model: 'Gol',
      color: 'PRATA',
      plate: null,
      reason: 'faróis acesos',
    })
  })

  it('tira o # do começo mesmo depois de linhas em branco', () => {
    expect(parseAlert('\n#gol branco ffo4745')).toMatchObject({ model: 'gol', freeText: 'gol branco ffo4745' })
  })

  it('o texto livre fica numa linha só, sem o # do começo', () => {
    expect(parseAlert('#reunião hoje |  às 19h\nna sala 2 ').freeText).toBe('reunião hoje | às 19h na sala 2')
  })
})

describe('vocabulário', () => {
  it('cor: minúsculas e sem acento', () => {
    expect(isColor('Prata')).toBe(true)
    expect(isColor('CINZA')).toBe(true)
    expect(isColor('corolla')).toBe(false)
  })

  it('início de motivo: lista e prefixos, minúsculas e sem acento', () => {
    for (const word of ['farol', 'Faróis', 'estacionado', 'parada', 'compareça', 'compareser', 'bloqueando']) {
      expect(isReasonStart(word), word).toBe(true)
    }
    for (const word of ['gol', 'para', 'portão', 'branco']) {
      expect(isReasonStart(word), word).toBe(false)
    }
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
