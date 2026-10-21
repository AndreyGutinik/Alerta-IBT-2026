import { describe, expect, it } from 'vitest'
import { normalizeRequest, normalizeUser } from './normalize'

describe('normalizeUser', () => {
  it('não objeto vira null', () => {
    for (const v of [null, undefined, 'x', 3, [1]]) expect(normalizeUser(v)).toBeNull()
  })
  it('copia os campos conhecidos e dá padrão seguro (member, não bloqueado)', () => {
    expect(normalizeUser({})).toEqual({ name: '', email: '', role: 'member', blocked: false, createdAt: 0 })
    expect(
      normalizeUser({
        name: 'Ana',
        email: 'a@x.com',
        role: 'admin',
        blocked: true,
        createdAt: 5,
        lastSeenAt: 6,
        lastSentAt: 7,
        lastRequestId: 'r',
        sentCount: 3,
        extra: 1,
      }),
    ).toEqual({
      name: 'Ana',
      email: 'a@x.com',
      role: 'admin',
      blocked: true,
      createdAt: 5,
      lastSeenAt: 6,
      lastSentAt: 7,
      lastRequestId: 'r',
      sentCount: 3,
    })
  })
  it('role desconhecido e blocked "true" (texto) não dão privilégio nem bloqueio', () => {
    const r = normalizeUser({ role: 'root', blocked: 'true' })
    expect(r?.role).toBe('member')
    expect(r?.blocked).toBe(false)
  })
})

describe('normalizeRequest', () => {
  it('sem raw de texto vira null', () => {
    expect(normalizeRequest({ uid: 'u' })).toBeNull()
    expect(normalizeRequest(null)).toBeNull()
  })
  it('pedido novo (sem status) e completo', () => {
    expect(normalizeRequest({ uid: 'u', name: 'Ana', raw: 'x', urgent: true, createdAt: 9 })).toEqual({
      uid: 'u',
      name: 'Ana',
      raw: 'x',
      urgent: true,
      createdAt: 9,
      cancel: false,
    })
    const full = normalizeRequest({
      uid: 'u',
      name: 'Ana',
      raw: 'x',
      urgent: false,
      createdAt: 9,
      cancel: true,
      preview: { text: 'T', kind: 'free', by: 'gemini', plate: 'ABC-1234' },
      status: { state: 'queued', position: 2, reason: 'r', text: 't', at: 11 },
    })
    expect(full?.cancel).toBe(true)
    expect(full?.preview).toEqual({ text: 'T', kind: 'free', by: 'gemini', plate: 'ABC-1234' })
    expect(full?.status).toEqual({ state: 'queued', position: 2, reason: 'r', text: 't', at: 11 })
  })
  it('status com estado inválido é ignorado', () => {
    expect(normalizeRequest({ raw: 'x', status: { state: 'weird', at: 1 } })?.status).toBeUndefined()
  })
})
