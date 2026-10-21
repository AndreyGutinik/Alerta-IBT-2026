import { useEffect, useMemo, useState } from 'react'
import { isBlank } from '../alerts/format'
import { parseAlert } from '../alerts/parser'
import type { Role } from '../alerts/types'
import { correctAlert } from '../correction/correct'
import type { AiCorrector, Correction } from '../correction/types'
import { useLive, useServerNow } from '../state/AppData'
import { evaluatePreview, isRawTooLong, type PreviewResult, tooLongResult } from './evaluate'

export const PREVIEW_DEBOUNCE_MS = 700

export type PreviewStatus = 'empty' | 'busy' | 'ready'

interface Done {
  raw: string
  corrector: AiCorrector
  correction: Correction
}

/**
 * A prévia do texto digitado. Sem IA, o parser local responde na hora. Com IA, espera 700 ms depois que a
 * pessoa para de digitar e então corrige (com tempo máximo e reserva local); enquanto isso fica `busy` e o
 * Enviar não tem prévia. Resposta de uma chamada velha é descartada: a prévia é sempre a do texto mais novo.
 */
export function usePreview(
  raw: string,
  role: Role,
  corrector: AiCorrector | null,
  opts: { debounceMs?: number; timeoutMs?: number } = {},
): { status: PreviewStatus; result: PreviewResult | null } {
  const live = useLive()
  const serverNow = useServerNow()
  const { debounceMs = PREVIEW_DEBOUNCE_MS, timeoutMs } = opts
  const [done, setDone] = useState<Done | null>(null)

  const blank = isBlank(raw)
  const tooLong = !blank && isRawTooLong(raw, role)
  const needsAi = corrector !== null && !blank && !tooLong

  useEffect(() => {
    if (!needsAi || corrector === null) return undefined
    let cancelled = false
    const timer = setTimeout(() => {
      void correctAlert(raw, corrector, timeoutMs).then((correction) => {
        if (!cancelled) setDone({ raw, corrector, correction })
      })
    }, debounceMs)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [raw, corrector, needsAi, debounceMs, timeoutMs])

  const local = useMemo<Correction | null>(
    () => (corrector === null && !blank ? { alert: parseAlert(raw), by: 'local', refused: false } : null),
    [raw, corrector, blank],
  )

  let correction: Correction | null = null
  if (!blank && !tooLong) {
    if (corrector === null) correction = local
    else if (done && done.raw === raw && done.corrector === corrector) correction = done.correction
  }

  const result = useMemo<PreviewResult | null>(() => {
    if (tooLong) return tooLongResult(raw)
    return correction ? evaluatePreview(correction, role, live, serverNow) : null
  }, [tooLong, raw, correction, role, live, serverNow])

  const status: PreviewStatus = blank ? 'empty' : result ? 'ready' : 'busy'
  return { status, result: blank ? null : result }
}
