import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SendInput } from '../data/types'
import { FakeBackend } from '../test/fakes'
import { sendAlert } from './sendFlow'

const input: SendInput = {
  uid: 'u1',
  name: 'Joana',
  raw: 'gol branco ffo4745',
  urgent: false,
  preview: { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' },
}

afterEach(() => vi.useRealTimers())

describe('sendAlert', () => {
  it('sem conexão: erro na hora e o backend nem é chamado (nada fica escondido na fila do SDK)', async () => {
    const backend = new FakeBackend()
    const out = await sendAlert(backend, input, { connected: false })
    expect(out).toEqual({ ok: false, message: 'Sem conexão. Confira a internet e tente de novo.' })
    expect(backend.sent).toHaveLength(0)
  })

  it('envio que nunca responde: depois de 10 s vira erro', async () => {
    vi.useFakeTimers()
    const backend = new FakeBackend()
    backend.sendImpl = () => new Promise(() => {})
    const pending = sendAlert(backend, input, { connected: true })
    await vi.advanceTimersByTimeAsync(9999)
    let settled = false
    void pending.then(() => (settled = true))
    await vi.advanceTimersByTimeAsync(0)
    expect(settled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    expect(await pending).toEqual({
      ok: false,
      message: 'Não deu para enviar. Confira a internet e tente de novo.',
    })
  })

  it('o tempo é configurável', async () => {
    vi.useFakeTimers()
    const backend = new FakeBackend()
    backend.sendImpl = () => new Promise(() => {})
    const pending = sendAlert(backend, input, { connected: true, timeoutMs: 50 })
    await vi.advanceTimersByTimeAsync(50)
    expect((await pending).ok).toBe(false)
  })

  it.each([
    ['código PERMISSION_DENIED', { code: 'PERMISSION_DENIED' }],
    ['mensagem permission_denied', new Error('permission_denied')],
    ['mensagem em maiúsculas dentro de texto', new Error('Error: PERMISSION_DENIED: Permission denied')],
  ])('limite de 20 s das regras (%s)', async (_name, error) => {
    const backend = new FakeBackend()
    backend.sendImpl = () => Promise.reject(error)
    const out = await sendAlert(backend, input, { connected: true })
    expect(out).toEqual({ ok: false, message: 'Espere alguns segundos para mandar outro aviso.' })
  })

  it('outro erro', async () => {
    const backend = new FakeBackend()
    backend.sendImpl = () => Promise.reject(new Error('boom'))
    expect(await sendAlert(backend, input, { connected: true })).toEqual({
      ok: false,
      message: 'Não deu para enviar. Tente de novo.',
    })
  })

  it('erro lançado de forma síncrona também vira mensagem', async () => {
    const backend = new FakeBackend()
    backend.sendImpl = () => {
      throw new Error('boom')
    }
    expect((await sendAlert(backend, input, { connected: true })).ok).toBe(false)
  })

  it('sucesso devolve o id', async () => {
    const backend = new FakeBackend()
    backend.sendImpl = () => Promise.resolve('r9')
    expect(await sendAlert(backend, input, { connected: true })).toEqual({ ok: true, id: 'r9' })
    expect(backend.sent).toEqual([input])
  })
})
