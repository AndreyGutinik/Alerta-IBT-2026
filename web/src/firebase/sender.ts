import type { SendInput } from '../data/types'

/** Sem conexão: o envio falha na hora (o SDK do banco guardaria a escrita em silêncio e a faria depois). */
export class OfflineError extends Error {
  readonly code = 'offline'
  constructor() {
    super('offline')
  }
}

export interface SenderDeps {
  isConnected(): boolean
  /** Um id novo de pedido, gerado no aparelho (sem rede). */
  newKey(): string
  /** Grava o pedido (e o envio da pessoa) com esse id; resolve quando o servidor confirmou. */
  write(id: string, input: SendInput): Promise<void>
  now?(): number
  /** Resposta que demora mais que isto já foi dada como falha para a pessoa (o tempo do `sendFlow`). */
  lateAfterMs?: number
}

/** Um envio ainda sem resposta só é reaproveitado por esse tempo (depois vale um id novo). */
export const REUSE_PENDING_MS = 120_000
/** Um envio que só terminou depois de a pessoa ver "falhou" responde "enviado" a quem tentar de novo por esse tempo. */
export const REUSE_LATE_MS = 60_000

interface Entry {
  id: string
  startedAt: number
  done: Promise<string>
  /** Preenchido quando o envio terminou com sucesso só depois do tempo (ver `lateAfterMs`). */
  lateUntil?: number
}

function fingerprint(input: SendInput): string {
  return JSON.stringify([input.uid, input.urgent, input.raw])
}

/**
 * O envio de um pedido com id gerado no aparelho. Regras:
 * - sem conexão: erro na hora, nada é gravado nem fica na fila do SDK;
 * - repetir o mesmo texto enquanto o primeiro envio não respondeu devolve o MESMO envio (mesmo id, uma
 *   escrita só), então tentar de novo com a rede instável não duplica;
 * - se o primeiro só terminou depois do tempo (a pessoa já viu "não deu para enviar"), tentar de novo logo
 *   em seguida responde com o mesmo id em vez de mandar o aviso duas vezes.
 */
export class RequestSender {
  private entries = new Map<string, Entry>()
  private readonly now: () => number
  private readonly lateAfterMs: number

  constructor(private readonly deps: SenderDeps) {
    this.now = deps.now ?? Date.now
    this.lateAfterMs = deps.lateAfterMs ?? 10_000
  }

  /** Esquece os envios tardios cujo prazo acabou (a lista não cresce). */
  private sweep(now: number): void {
    for (const [key, e] of this.entries) if (e.lateUntil !== undefined && now > e.lateUntil) this.entries.delete(key)
  }

  send(input: SendInput): Promise<string> {
    if (!this.deps.isConnected()) return Promise.reject(new OfflineError())

    const key = fingerprint(input)
    const now = this.now()
    this.sweep(now)
    const existing = this.entries.get(key)
    if (existing) {
      const reusable =
        existing.lateUntil !== undefined ? now <= existing.lateUntil : now - existing.startedAt <= REUSE_PENDING_MS
      if (reusable) return existing.done
      this.entries.delete(key)
    }

    const id = this.deps.newKey()
    const entry: Entry = { id, startedAt: now, done: Promise.resolve('') }
    entry.done = (async () => {
      await this.deps.write(id, input)
      return id
    })()
    this.entries.set(key, entry)

    const drop = () => {
      if (this.entries.get(key) === entry) this.entries.delete(key)
    }
    entry.done.then(
      () => {
        const finished = this.now()
        if (finished - entry.startedAt > this.lateAfterMs) entry.lateUntil = finished + REUSE_LATE_MS
        else drop()
      },
      drop,
    )
    return entry.done
  }
}
