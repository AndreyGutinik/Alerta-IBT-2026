import { formatFree, formatVehicle } from '../alerts/format'
import { decide, isTooLong } from '../alerts/rules'
import type { Field, RuleKind, SenderRole } from '../alerts/types'
import { type Duplicate, findDuplicate } from '../data/live'
import type { LiveState } from '../data/types'
import { correctAlert } from './correct'
import type { AiCorrector, CorrectedBy } from './types'

/**
 * A prévia do aviso (o que a tela mostra antes de enviar). É só uma sugestão: o PC refaz tudo a partir do
 * texto digitado, com o filtro de palavrões (que o site não tem, de propósito) e as regras fixas dele.
 */
export interface Preview {
  /** O texto final em MAIÚSCULAS; vazio quando não vai para o telão. */
  text: string
  kind: RuleKind
  missing: Field[]
  by: CorrectedBy
  /** A placa em forma de exibição, só no aviso de veículo. */
  plate?: string
  /** Mesma placa já na fila, passando ou exibida há menos de 10 min (membro: bloqueia; admin: só avisa). */
  duplicate?: Duplicate
  /** A IA recusou o texto: a tela mostra só uma nota neutra ("conteúdo impróprio"). */
  refused?: boolean
  /** Membro com texto formatado acima do limite (200 letras): não vai, antes de qualquer correção. */
  tooLong?: boolean
}

export interface PreviewContext {
  role: SenderRole
  live: LiveState | null
  serverNow: number
  timeoutMs?: number
}

/**
 * Monta a prévia na mesma ordem do PC: texto grande demais de membro é recusado antes de qualquer correção;
 * depois a correção (IA com trava, ou parser local), a recusa da IA, a regra do papel e o repetido.
 */
export async function buildPreview(raw: string, corrector: AiCorrector | null, ctx: PreviewContext): Promise<Preview> {
  const cleaned = formatFree(raw) || raw.trim()
  if (isTooLong(cleaned, ctx.role)) {
    return { text: '', kind: 'reject', missing: [], by: 'local', tooLong: true }
  }

  const correction = await correctAlert(raw, corrector, ctx.timeoutMs)
  if (correction.refused) {
    return { text: '', kind: 'reject', missing: [], by: correction.by, refused: true }
  }

  const alert = correction.alert
  const decision = decide(alert, ctx.role)
  const text =
    decision.kind === 'vehicle' ? formatVehicle(alert) : decision.kind === 'free' ? formatFree(alert.freeText) : ''
  // A correção pode trazer palavra que não foi digitada: o limite vale também para o texto final.
  if (isTooLong(text, ctx.role)) {
    return { text: '', kind: 'reject', missing: decision.missing, by: correction.by, tooLong: true }
  }

  const preview: Preview = { text, kind: decision.kind, missing: decision.missing, by: correction.by }
  if (decision.kind === 'vehicle' && alert.plate) {
    preview.plate = alert.plate
    const duplicate = findDuplicate(alert.plate, ctx.live, ctx.serverNow)
    if (duplicate) preview.duplicate = duplicate
  }
  return preview
}
