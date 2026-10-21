import { isBlank } from '../alerts/format'
import { parseAlert } from '../alerts/parser'
import { guardAiResult } from './guard'
import type { AiAlert, AiCorrector, Correction } from './types'

/**
 * Tempo máximo da IA na prévia. O primeiro pedido depois de uns minutos parado demora mais, por isso 5 s
 * como no PC (spec 5.3); a espera de 700 ms depois de digitar é da tela.
 */
export const AI_TIMEOUT_MS = 5000

/**
 * Corrige o texto: o parser local roda sempre; se há IA, ela responde dentro do tempo máximo e a resposta
 * passa pela trava de segurança. Sem IA, erro, tempo esgotado ou resposta recusada pela trava → `by: 'local'`.
 * Ao estourar o tempo o `signal` do corretor é abortado.
 */
export async function correctAlert(
  raw: string,
  corrector: AiCorrector | null,
  timeoutMs: number = AI_TIMEOUT_MS,
): Promise<Correction> {
  const local = parseAlert(raw)
  if (corrector === null || isBlank(raw)) return { alert: local, by: 'local', refused: false }

  const ai = await ask(raw, corrector, timeoutMs)
  return guardAiResult(raw, ai, local)
}

// A resposta da IA, ou null quando ela falhou, lançou erro ou passou do tempo.
async function ask(raw: string, corrector: AiCorrector, timeoutMs: number): Promise<AiAlert | null> {
  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined
  const expired = new Promise<null>((resolve) => {
    timer = setTimeout(() => {
      controller.abort()
      resolve(null)
    }, timeoutMs)
  })

  let call: Promise<AiAlert | null>
  try {
    call = corrector.correct(raw, controller.signal)
  } catch {
    clearTimeout(timer)
    return null
  }
  // Uma resposta que chega depois do tempo ou com erro não pode virar rejeição sem dono.
  const safeCall = call.catch(() => null)
  try {
    return await Promise.race([safeCall, expired])
  } finally {
    clearTimeout(timer)
  }
}
