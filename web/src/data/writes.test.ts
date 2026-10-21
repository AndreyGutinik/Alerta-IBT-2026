import { increment, serverTimestamp } from 'firebase/database'
import { describe, expect, it } from 'vitest'
import { buildSeenUpdate, buildSendUpdate, buildUserRecord } from './writes'

describe('buildUserRecord', () => {
  it('registro novo: member, não bloqueado, horas do servidor', () => {
    expect(buildUserRecord({ uid: 'u1', name: 'Maria', email: 'maria@example.com' })).toEqual({
      name: 'Maria',
      email: 'maria@example.com',
      role: 'member',
      blocked: false,
      createdAt: serverTimestamp(),
      lastSeenAt: serverTimestamp(),
    })
  })

  it('nome vazio ou só espaços usa o e-mail', () => {
    expect(buildUserRecord({ uid: 'u1', name: '', email: 'maria@example.com' }).name).toBe('maria@example.com')
    expect(buildUserRecord({ uid: 'u1', name: '   ', email: 'maria@example.com' }).name).toBe('maria@example.com')
  })

  it('nome de 100 caracteres vira 80, sem cortar um emoji ao meio', () => {
    expect(buildUserRecord({ uid: 'u1', name: 'a'.repeat(100), email: 'm@x.com' }).name).toBe('a'.repeat(80))
    const cut = buildUserRecord({ uid: 'u1', name: 'a'.repeat(79) + '\u{1F697}' + 'b', email: 'm@x.com' }).name as string
    expect(cut).toBe('a'.repeat(79))
  })
})

describe('buildSendUpdate', () => {
  const preview = { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' } as const
  const input = { uid: 'u1', name: 'Maria', raw: 'gol branco ffo4745', urgent: true, preview }

  it('tem exatamente as chaves dos quatro caminhos, a partir da raiz', () => {
    const update = buildSendUpdate('r1', input)

    expect(Object.keys(update).sort()).toEqual(
      ['requests/r1', 'users/u1/lastRequestId', 'users/u1/lastSentAt', 'users/u1/sentCount'].sort(),
    )
    expect(update['users/u1/lastSentAt']).toEqual(serverTimestamp())
    expect(update['users/u1/lastRequestId']).toBe('r1')
    expect(update['users/u1/sentCount']).toEqual(increment(1))
    expect(update['requests/r1']).toEqual({
      uid: 'u1',
      name: 'Maria',
      raw: 'gol branco ffo4745',
      urgent: true,
      createdAt: serverTimestamp(),
      cancel: false,
      preview: { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' },
    })
  })

  it('o pedido novo não leva status', () => {
    expect(buildSendUpdate('r1', input)['requests/r1']).not.toHaveProperty('status')
  })

  it('sem prévia não há a chave preview', () => {
    expect(buildSendUpdate('r1', { ...input, preview: null })['requests/r1']).not.toHaveProperty('preview')
  })

  it('prévia sem placa não tem a chave plate', () => {
    const free = { text: 'AVISO', kind: 'free', by: 'gemini' } as const
    const request = buildSendUpdate('r1', { ...input, preview: free })['requests/r1'] as { preview: object }

    expect(request.preview).toEqual({ text: 'AVISO', kind: 'free', by: 'gemini' })
    expect(request.preview).not.toHaveProperty('plate')
  })

  it('nome do pedido é cortado em 80', () => {
    const request = buildSendUpdate('r1', { ...input, name: 'n'.repeat(120) })['requests/r1'] as { name: string }

    expect(request.name).toBe('n'.repeat(80))
  })
})

describe('buildSeenUpdate', () => {
  it('atualiza só o nome e o último uso (hora do servidor)', () => {
    const update = buildSeenUpdate({ uid: 'u1', name: '  Maria  ', email: 'maria@example.com' })
    expect(Object.keys(update).sort()).toEqual(['lastSeenAt', 'name'])
    expect(update.name).toBe('Maria')
    expect(update.lastSeenAt).toEqual({ '.sv': 'timestamp' })
  })

  it('sem nome usa o e-mail e nunca passa de 80 caracteres', () => {
    expect(buildSeenUpdate({ uid: 'u', name: '', email: 'a@b.com' }).name).toBe('a@b.com')
    expect((buildSeenUpdate({ uid: 'u', name: 'x'.repeat(200), email: '' }).name as string).length).toBe(80)
  })
})
