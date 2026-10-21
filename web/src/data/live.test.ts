import { describe, expect, it } from 'vitest'
import {
  findDuplicate,
  headerPill,
  isPcOnline,
  normalizeLive,
  plateFromText,
  rateLimitLeft,
} from './live'
import type { LiveItem, LiveRecent, LiveState, UserRecord } from './types'

const NOW = 5_000_000

function live(partial: Partial<LiveState> = {}): LiveState {
  return {
    heartbeatAt: NOW,
    led: true,
    paused: false,
    now: null,
    queue: [],
    recent: [],
    bar: { position: 'top', height: 56, speed: 90 },
    ...partial,
  }
}

const a: LiveItem = { id: 'a', text: 'GOL BRANCO | PLACA: FFO-4745', source: 'site' }
const b: LiveItem = { id: 'b', text: 'ONIX PRETO | PLACA: QWE-1B23', source: 'telegram', urgent: true, plate: 'QWE-1B23' }

describe('normalizeLive', () => {
  it('null e valores que não são objeto viram null', () => {
    expect(normalizeLive(null)).toBeNull()
    expect(normalizeLive(undefined)).toBeNull()
    expect(normalizeLive('x')).toBeNull()
    expect(normalizeLive([1])).toBeNull()
  })

  it('queue como objeto de chaves numéricas vira array na ordem', () => {
    expect(normalizeLive({ heartbeatAt: 1, queue: { 1: b, 0: a } })?.queue).toEqual([a, b])
    expect(normalizeLive({ heartbeatAt: 1, queue: { 0: a, 10: b, 2: a } })?.queue.map((q) => q.id)).toEqual(['a', 'a', 'b'])
  })

  it('queue como array (com buraco) também funciona', () => {
    expect(normalizeLive({ heartbeatAt: 1, queue: [a, null, b] })?.queue).toEqual([a, b])
  })

  it('sem queue nem recent: listas vazias; sem now: null; sem bar: padrão', () => {
    expect(normalizeLive({ heartbeatAt: 1 })).toEqual({
      heartbeatAt: 1,
      led: false,
      paused: false,
      now: null,
      queue: [],
      recent: [],
      bar: { position: 'top', height: 56, speed: 90 },
    })
  })

  it('bar parcial ou ruim ganha o padrão nos campos que faltam; fontSize só se for número positivo', () => {
    expect(normalizeLive({ bar: { position: 'bottom', height: 80, speed: 120, fontSize: 40 } })?.bar).toEqual({
      position: 'bottom',
      height: 80,
      speed: 120,
      fontSize: 40,
    })
    expect(normalizeLive({ bar: { position: 'meio', height: -1, fontSize: 0 } })?.bar).toEqual({
      position: 'top',
      height: 56,
      speed: 90,
    })
  })

  it('now, recent e campos opcionais', () => {
    const value = normalizeLive({
      heartbeatAt: 10,
      version: '8.0.0',
      led: true,
      paused: true,
      gemini: 'online',
      now: { id: 'n', text: 'X', source: 'pc', startedAt: 3, plate: 'ABC-1234' },
      recent: [
        { id: 'r1', text: 'Y', state: 'shown', at: 4 },
        { id: 'r2', text: 'Z', state: 'invalido', at: 5 },
      ],
    })

    expect(value).toMatchObject({
      version: '8.0.0',
      led: true,
      paused: true,
      gemini: 'online',
      now: { id: 'n', text: 'X', source: 'pc', startedAt: 3, plate: 'ABC-1234' },
      recent: [{ id: 'r1', text: 'Y', state: 'shown', at: 4 }],
    })
  })

  it('origem desconhecida vira pc', () => {
    expect(normalizeLive({ queue: [{ id: 'q', text: 'X', source: 'fax' }] })?.queue[0].source).toBe('pc')
  })
})

describe('isPcOnline', () => {
  it('90 s exatos ainda é online; 90,001 s não', () => {
    expect(isPcOnline(live({ heartbeatAt: 1_000_000 }), 1_090_000)).toBe(true)
    expect(isPcOnline(live({ heartbeatAt: 1_000_000 }), 1_090_001)).toBe(false)
  })

  it('sem /live ou sem heartbeat é offline', () => {
    expect(isPcOnline(null, 1)).toBe(false)
    expect(isPcOnline(normalizeLive({ led: true }), NOW)).toBe(false)
  })
})

describe('plateFromText', () => {
  it('lê PLACA: no texto do aviso', () => {
    expect(plateFromText('CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO')).toBe('GBR-9003')
    expect(plateFromText('PLACA: BRA-2E19')).toBe('BRA-2E19')
  })

  it('aviso livre não tem placa', () => {
    expect(plateFromText('AVISO LIVRE')).toBeNull()
  })
})

describe('findDuplicate', () => {
  const min = 60_000

  it('sem placa ou sem /live nunca é repetido', () => {
    expect(findDuplicate(null, live({ queue: [a] }), NOW)).toBeNull()
    expect(findDuplicate('FFO-4745', null, NOW)).toBeNull()
  })

  it('na fila: pelo texto ou pelo campo plate', () => {
    expect(findDuplicate('FFO-4745', live({ queue: [a, b] }), NOW)).toEqual({ where: 'queue', label: 'GOL BRANCO' })
    expect(findDuplicate('QWE-1B23', live({ queue: [a, b] }), NOW)).toEqual({ where: 'queue', label: 'ONIX PRETO' })
  })

  it('passando agora, só pelo texto', () => {
    const now = { id: 'n', text: 'CRETA PRETO | PLACA: GBR-9003', source: 'pc' as const, startedAt: NOW }

    expect(findDuplicate('GBR-9003', live({ now }), NOW)).toEqual({ where: 'now', label: 'CRETA PRETO' })
  })

  it('exibido há 9 min é repetido; há 11 min não; no limite de 10 min ainda é', () => {
    const shown = (ago: number): LiveRecent => ({ id: 'r', text: 'GOL BRANCO | PLACA: FFO-4745', state: 'shown', at: NOW - ago })

    expect(findDuplicate('FFO-4745', live({ recent: [shown(9 * min)] }), NOW)).toEqual({
      where: 'recent',
      label: 'GOL BRANCO',
      minutesAgo: 9,
    })
    expect(findDuplicate('FFO-4745', live({ recent: [shown(10 * min)] }), NOW)?.minutesAgo).toBe(10)
    expect(findDuplicate('FFO-4745', live({ recent: [shown(11 * min)] }), NOW)).toBeNull()
  })

  it('minutesAgo é pelo menos 1', () => {
    const recent: LiveRecent = { id: 'r', text: 'X | PLACA: FFO-4745', state: 'shown', at: NOW - 5_000 }

    expect(findDuplicate('FFO-4745', live({ recent: [recent] }), NOW)?.minutesAgo).toBe(1)
  })

  it('recusado, cancelado e expirado não contam', () => {
    for (const state of ['rejected', 'cancelled', 'expired'] as const) {
      const recent: LiveRecent = { id: 'r', text: 'X | PLACA: FFO-4745', state, at: NOW - min }

      expect(findDuplicate('FFO-4745', live({ recent: [recent] }), NOW), state).toBeNull()
    }
  })

  it('fila ganha de passando, que ganha de exibido', () => {
    const now = { id: 'n', text: 'A | PLACA: FFO-4745', source: 'pc' as const, startedAt: NOW }
    const recent: LiveRecent = { id: 'r', text: 'B | PLACA: FFO-4745', state: 'shown', at: NOW - min }

    expect(findDuplicate('FFO-4745', live({ queue: [a], now, recent: [recent] }), NOW)?.where).toBe('queue')
    expect(findDuplicate('FFO-4745', live({ now, recent: [recent] }), NOW)?.where).toBe('now')
  })

  it('placa em minúsculas também casa', () => {
    expect(findDuplicate('ffo-4745', live({ queue: [a] }), NOW)?.where).toBe('queue')
  })
})

describe('headerPill', () => {
  const ok = live()

  it('desconectado: Sem conexão (vence tudo)', () => {
    expect(headerPill({ connected: false, live: null, serverNow: NOW })).toEqual({ tone: 'bad', label: 'Sem conexão' })
    expect(headerPill({ connected: false, live: ok, serverNow: NOW })).toEqual({ tone: 'bad', label: 'Sem conexão' })
  })

  it('heartbeat de 91 s ou sem /live: PC offline', () => {
    expect(headerPill({ connected: true, live: ok, serverNow: NOW + 91_000 })).toEqual({ tone: 'bad', label: 'PC offline' })
    expect(headerPill({ connected: true, live: null, serverNow: NOW })).toEqual({ tone: 'bad', label: 'PC offline' })
  })

  it('pausado: Barra pausada, antes de Telão desligado', () => {
    expect(headerPill({ connected: true, live: live({ paused: true, led: false }), serverNow: NOW })).toEqual({
      tone: 'warn',
      label: 'Barra pausada',
    })
  })

  it('led desligado: Telão desligado', () => {
    expect(headerPill({ connected: true, live: live({ led: false }), serverNow: NOW })).toEqual({
      tone: 'bad',
      label: 'Telão desligado',
    })
  })

  it('tudo certo: Telão ligado', () => {
    expect(headerPill({ connected: true, live: ok, serverNow: NOW })).toEqual({ tone: 'ok', label: 'Telão ligado' })
  })
})

describe('rateLimitLeft', () => {
  const record = (lastSentAt?: number): UserRecord => ({
    name: 'Maria',
    email: 'm@x.com',
    role: 'member',
    blocked: false,
    createdAt: 1,
    lastSentAt,
  })

  it('membro: segundos que faltam, arredondados para cima', () => {
    expect(rateLimitLeft(record(NOW - 5_000), 'member', NOW)).toBe(15)
    expect(rateLimitLeft(record(NOW - 5_001), 'member', NOW)).toBe(15)
    expect(rateLimitLeft(record(NOW - 19_999), 'member', NOW)).toBe(1)
  })

  it('passou dos 20 s, nunca enviou, admin ou sem registro: 0', () => {
    expect(rateLimitLeft(record(NOW - 20_000), 'member', NOW)).toBe(0)
    expect(rateLimitLeft(record(NOW - 25_000), 'member', NOW)).toBe(0)
    expect(rateLimitLeft(record(undefined), 'member', NOW)).toBe(0)
    expect(rateLimitLeft(record(NOW - 1_000), 'admin', NOW)).toBe(0)
    expect(rateLimitLeft(null, 'member', NOW)).toBe(0)
  })
})
