import { describe, expect, it } from 'vitest'
import { requestBadge, requestText } from './requestView'
import { formatHm } from './time'
import type { LiveState, RequestRecord, RequestStatus } from './types'

function request(status?: Partial<RequestStatus> & { state: RequestStatus['state'] }, extra: Partial<RequestRecord> = {}): RequestRecord {
  return {
    uid: 'u1',
    name: 'Maria',
    raw: 'gol branco ffo4745',
    urgent: false,
    createdAt: 1,
    cancel: false,
    ...(status ? { status: { at: 1000, ...status } } : {}),
    ...extra,
  }
}

const live = (led: boolean): LiveState => ({
  heartbeatAt: 1,
  led,
  paused: false,
  now: null,
  queue: [],
  recent: [],
  bar: { position: 'top', height: 56, speed: 90 },
})

describe('requestBadge', () => {
  it('sem status: enviado, pode cancelar', () => {
    expect(requestBadge(request(), null)).toEqual({ tone: 'q', icon: 'clock', label: 'enviado', canCancel: true, struck: false })
    expect(requestBadge(request({ state: 'pending' }), null).label).toBe('enviado')
  })

  it('na fila com posição: "2º na fila"', () => {
    const badge = requestBadge(request({ state: 'queued', position: 2 }), live(true))

    expect(badge).toMatchObject({ tone: 'q', icon: 'clock', label: '2º na fila', canCancel: true })
  })

  it('na fila com LED desligado ou sem posição: "na fila"', () => {
    expect(requestBadge(request({ state: 'queued', position: 2 }), live(false)).label).toBe('na fila')
    expect(requestBadge(request({ state: 'queued' }), live(true)).label).toBe('na fila')
  })

  it('passando: sem cancelar', () => {
    expect(requestBadge(request({ state: 'showing' }), null)).toMatchObject({
      tone: 'now',
      icon: 'monitor-play',
      label: 'passando',
      canCancel: false,
    })
  })

  it('exibido: hh:mm da hora em que passou', () => {
    const at = new Date(2026, 9, 19, 19, 4).getTime()

    expect(requestBadge(request({ state: 'shown', at }), null)).toMatchObject({ tone: 'ok', icon: 'check', label: formatHm(at), canCancel: false })
    expect(formatHm(at)).toBe('19:04')
  })

  it('recusado: motivo do PC, ou "recusado"', () => {
    expect(requestBadge(request({ state: 'rejected', reason: 'faltou placa' }), null)).toMatchObject({
      tone: 'no',
      icon: 'x',
      label: 'faltou placa',
    })
    expect(requestBadge(request({ state: 'rejected' }), null).label).toBe('recusado')
  })

  it('cancelado: com ou sem motivo, riscado', () => {
    expect(requestBadge(request({ state: 'cancelled', reason: 'pregação' }), null)).toMatchObject({
      tone: 'cx',
      icon: 'ban',
      label: 'cancelado: pregação',
      struck: true,
    })
    expect(requestBadge(request({ state: 'cancelled' }), null).label).toBe('cancelado')
  })

  it('expirado', () => {
    expect(requestBadge(request({ state: 'expired' }), null)).toMatchObject({
      tone: 'cx',
      icon: 'hourglass',
      label: 'expirou',
      canCancel: false,
    })
  })

  it('cancel gravado e o PC ainda não respondeu: "cancelando…", sem cancelar de novo', () => {
    expect(requestBadge(request(undefined, { cancel: true }), null)).toMatchObject({ label: 'cancelando…', canCancel: false })
    expect(requestBadge(request({ state: 'queued', position: 1 }, { cancel: true }), null).label).toBe('cancelando…')
  })

  it('cancel gravado mas o PC já decidiu: vale o status do PC', () => {
    expect(requestBadge(request({ state: 'cancelled' }, { cancel: true }), null).label).toBe('cancelado')
    expect(requestBadge(request({ state: 'showing' }, { cancel: true }), null).label).toBe('passando')
  })
})

describe('requestText', () => {
  const preview = { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' } as const

  it('status.text vence a prévia, que vence o texto digitado em maiúsculas', () => {
    expect(requestText(request({ state: 'queued', text: 'FINAL DO PC' }, { preview }))).toBe('FINAL DO PC')
    expect(requestText(request({ state: 'queued' }, { preview }))).toBe(preview.text)
    expect(requestText(request())).toBe('GOL BRANCO FFO4745')
  })
})
