import type { Backend } from '../data/backend'
import type { SendInput } from '../data/types'

export type SendOutcome = { ok: true; id: string } | { ok: false; message: string }

export const SEND_TIMEOUT_MS = 10_000

export const MSG_OFFLINE = 'Sem conexão. Confira a internet e tente de novo.'
export const MSG_TIMEOUT = 'Não deu para enviar. Confira a internet e tente de novo.'
export const MSG_RATE = 'Espere alguns segundos para mandar outro aviso.'
export const MSG_FAILED = 'Não deu para enviar. Tente de novo.'

function isOffline(e: unknown): boolean {
  return typeof e === 'object' && e !== null && (e as { code?: unknown }).code === 'offline'
}

function isPermissionDenied(e: unknown): boolean {
  if (typeof e !== 'object' || e === null) return false
  const { code, message } = e as { code?: unknown; message?: unknown }
  return [code, message].some((v) => typeof v === 'string' && v.toLowerCase().includes('permission_denied'))
}

/**
 * Manda o pedido. O SDK do banco guarda escrita offline em silêncio e a conclui quando a rede volta, por
 * isso: sem conexão nem tenta (erro na hora, nada fica escondido na fila) e, se a rede cair no meio, a
 * resposta passa do tempo (10 s) vira erro para a pessoa. As regras recusam o 2º aviso em menos de 20 s
 * com `permission_denied`.
 */
export async function sendAlert(
  backend: Backend,
  input: SendInput,
  opts: { connected: boolean; timeoutMs?: number },
): Promise<SendOutcome> {
  if (!opts.connected) return { ok: false, message: MSG_OFFLINE }

  let timer: ReturnType<typeof setTimeout> | undefined
  const expired = new Promise<'timeout'>((resolve) => {
    timer = setTimeout(() => resolve('timeout'), opts.timeoutMs ?? SEND_TIMEOUT_MS)
  })
  try {
    // Uma resposta tardia ou com erro depois do tempo não pode virar rejeição sem dono.
    const call = backend.send(input)
    call.catch(() => {})
    const winner = await Promise.race([call, expired])
    if (winner === 'timeout') return { ok: false, message: MSG_TIMEOUT }
    return { ok: true, id: winner }
  } catch (e) {
    if (isOffline(e)) return { ok: false, message: MSG_OFFLINE }
    return { ok: false, message: isPermissionDenied(e) ? MSG_RATE : MSG_FAILED }
  } finally {
    clearTimeout(timer)
  }
}
