import { describe, expect, it } from 'vitest'
import type { SendInput } from '../data/types'
import { OfflineError, REUSE_LATE_MS, REUSE_PENDING_MS, RequestSender } from './sender'

/**
 * Um banco falso que imita o SDK do Realtime Database: sem rede ou com a conexão caída no meio do envio, a
 * escrita fica na fila em silêncio e só termina quando a rede volta; recusa um id que já existe (as regras).
 */
class FakeRtdb {
  connected = true
  /** Escritas feitas com a conexão "ligada" mas que não foram confirmadas (caiu no meio do caminho). */
  hold = false
  requests = new Map<string, SendInput>()
  writeCalls: string[] = []
  private queue: { id: string; input: SendInput; resolve: () => void; reject: (e: unknown) => void }[] = []
  private counter = 0

  newKey = () => `req-${++this.counter}`

  write = (id: string, input: SendInput): Promise<void> => {
    this.writeCalls.push(id)
    return new Promise<void>((resolve, reject) => {
      if (this.connected && !this.hold) this.apply({ id, input, resolve, reject })
      else this.queue.push({ id, input, resolve, reject })
    })
  }

  private apply(w: { id: string; input: SendInput; resolve: () => void; reject: (e: unknown) => void }): void {
    if (this.requests.has(w.id)) {
      w.reject(Object.assign(new Error('PERMISSION_DENIED'), { code: 'PERMISSION_DENIED' }))
      return
    }
    this.requests.set(w.id, w.input)
    w.resolve()
  }

  /** A rede volta: o SDK despeja a fila na ordem. */
  reconnect(): void {
    this.connected = true
    this.hold = false
    for (const w of this.queue.splice(0)) this.apply(w)
  }

  get queued(): number {
    return this.queue.length
  }
}

const INPUT: SendInput = { uid: 'u1', name: 'Ana', raw: 'GOL BRANCO FFO4745', urgent: false, preview: null }

function setup(): { db: FakeRtdb; sender: RequestSender; clock: { t: number } } {
  const db = new FakeRtdb()
  const clock = { t: 1_000_000 }
  const sender = new RequestSender({
    isConnected: () => db.connected,
    newKey: db.newKey,
    write: db.write,
    now: () => clock.t,
    lateAfterMs: 10_000,
  })
  return { db, sender, clock }
}

describe('RequestSender', () => {
  it('sem conexão: falha na hora e nada vai para a fila do SDK', async () => {
    const { db, sender } = setup()
    db.connected = false
    await expect(sender.send(INPUT)).rejects.toBeInstanceOf(OfflineError)
    expect(db.writeCalls).toEqual([])
    expect(db.queued).toBe(0)
    db.reconnect()
    expect(db.requests.size).toBe(0)
  })

  it('com conexão grava com o id gerado no aparelho e devolve esse id', async () => {
    const { db, sender } = setup()
    await expect(sender.send(INPUT)).resolves.toBe('req-1')
    expect(db.requests.get('req-1')).toEqual(INPUT)
  })

  it('a conexão cai no meio e a pessoa repete o mesmo texto: o mesmo envio (uma escrita só, um pedido só)', async () => {
    const { db, sender } = setup()
    db.hold = true
    const first = sender.send(INPUT)
    const retry = sender.send(INPUT)
    expect(db.writeCalls).toEqual(['req-1'])
    db.reconnect()
    await expect(first).resolves.toBe('req-1')
    await expect(retry).resolves.toBe('req-1')
    expect(db.requests.size).toBe(1)
  })

  it('o envio termina depois do tempo (a pessoa já viu "falhou") e ela tenta de novo: responde enviado, sem 2º pedido', async () => {
    const { db, sender, clock } = setup()
    db.hold = true
    const first = sender.send(INPUT)
    clock.t += 30_000 // passou dos 10 s: a tela já mostrou o erro
    db.connected = false
    await expect(sender.send(INPUT)).rejects.toBeInstanceOf(OfflineError) // sem rede de novo: erro na hora
    db.reconnect()
    await first
    clock.t += 2_000
    await expect(sender.send(INPUT)).resolves.toBe('req-1')
    expect(db.writeCalls).toEqual(['req-1'])
    expect(db.requests.size).toBe(1)
  })

  it('passado o prazo do envio tardio, o mesmo texto vira um pedido novo', async () => {
    const { db, sender, clock } = setup()
    db.hold = true
    const first = sender.send(INPUT)
    clock.t += 30_000
    db.reconnect()
    await first
    clock.t += REUSE_LATE_MS + 1
    await expect(sender.send(INPUT)).resolves.toBe('req-2')
    expect(db.requests.size).toBe(2)
  })

  it('envio rápido e certo não é lembrado: o mesmo texto depois é outro pedido', async () => {
    const { db, sender } = setup()
    await sender.send(INPUT)
    await expect(sender.send(INPUT)).resolves.toBe('req-2')
    expect(db.requests.size).toBe(2)
  })

  it('recusado pelas regras: tentar de novo usa outro id', async () => {
    const keys: string[] = []
    let n = 0
    const denying = new RequestSender({
      isConnected: () => true,
      newKey: () => {
        n += 1
        keys.push(`k${n}`)
        return `k${n}`
      },
      write: () => Promise.reject(Object.assign(new Error('PERMISSION_DENIED'), { code: 'PERMISSION_DENIED' })),
    })
    await expect(denying.send(INPUT)).rejects.toThrow('PERMISSION_DENIED')
    await expect(denying.send(INPUT)).rejects.toThrow('PERMISSION_DENIED')
    expect(keys).toEqual(['k1', 'k2'])
  })

  it('textos, urgência ou pessoas diferentes não se misturam', async () => {
    const { db, sender } = setup()
    db.hold = true
    void sender.send(INPUT)
    void sender.send({ ...INPUT, urgent: true })
    void sender.send({ ...INPUT, raw: 'outro texto' })
    void sender.send({ ...INPUT, uid: 'u2' })
    expect(db.writeCalls).toHaveLength(4)
  })

  it('um envio parado há mais de 2 minutos não é mais reaproveitado', async () => {
    const { db, sender, clock } = setup()
    db.hold = true
    void sender.send(INPUT)
    clock.t += REUSE_PENDING_MS + 1
    void sender.send(INPUT)
    expect(db.writeCalls).toEqual(['req-1', 'req-2'])
  })

  it('erro síncrono do SDK vira rejeição', async () => {
    const sender = new RequestSender({
      isConnected: () => true,
      newKey: () => 'k',
      write: () => {
        throw new Error('boom')
      },
    })
    await expect(sender.send(INPUT)).rejects.toThrow('boom')
  })
})
