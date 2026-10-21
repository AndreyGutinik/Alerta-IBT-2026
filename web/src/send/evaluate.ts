import { formatFree } from '../alerts/format'
import { alertText, decide, isTooLong, MEMBER_MAX_LENGTH } from '../alerts/rules'
import type { Field, Role, RuleKind } from '../alerts/types'
import type { CorrectedBy, Correction } from '../correction/types'
import { type Duplicate, findDuplicate } from '../data/live'
import type { LiveState } from '../data/types'

/** A prévia que a tela mostra e que o Enviar usa. É só sugestão: o PC refaz tudo a partir do texto digitado. */
export interface PreviewResult {
  /** O texto final em MAIÚSCULAS; vazio quando não vai para o telão. */
  text: string
  kind: RuleKind
  /** O que falta (só para membro; admin não tem obrigatórios). */
  missing: Field[]
  /** A placa em forma de exibição, só no aviso de veículo. */
  plate: string | null
  by: CorrectedBy
  /** A IA recusou o texto (ofensivo, trote). */
  refused: boolean
  /** Membro com texto acima do limite de 200 letras. */
  tooLong: boolean
  /** Letras do texto final (ou do texto digitado, formatado, quando nem chegou a ter texto final). */
  length: number
  duplicate: Duplicate | null
  canSend: boolean
}

export { MEMBER_MAX_LENGTH }

/** Texto digitado, já formatado, maior do que o membro pode mandar? (Antes de qualquer correção, como o PC.) */
export function isRawTooLong(raw: string, role: Role): boolean {
  return isTooLong(formatFree(raw) || raw.trim(), role)
}

/** Resultado de texto grande demais, sem precisar chamar a IA. */
export function tooLongResult(raw: string): PreviewResult {
  return {
    text: '',
    kind: 'reject',
    missing: [],
    plate: null,
    by: 'local',
    refused: false,
    tooLong: true,
    length: (formatFree(raw) || raw.trim()).length,
    duplicate: null,
    canSend: false,
  }
}

/**
 * A prévia a partir da correção (IA com trava, ou parser local), do papel e do `/live`. Pura: quando o
 * `/live` muda, o repetido é recalculado sem chamar a IA de novo.
 */
export function evaluatePreview(c: Correction, role: Role, live: LiveState | null, serverNow: number): PreviewResult {
  if (c.refused) {
    return {
      text: '',
      kind: 'reject',
      missing: [],
      plate: null,
      by: c.by,
      refused: true,
      tooLong: false,
      length: 0,
      duplicate: null,
      canSend: false,
    }
  }

  const decision = decide(c.alert, role)
  const text = alertText(c.alert, decision)
  // A correção pode trazer palavra que não foi digitada: o limite vale também para o texto final.
  const tooLong = isTooLong(text, role)
  const plate = decision.kind === 'vehicle' ? c.alert.plate : null
  const duplicate = findDuplicate(plate, live, serverNow)

  const hasText = text.trim() !== ''
  const canSend =
    !tooLong &&
    hasText &&
    (role === 'admin' ? decision.kind !== 'reject' : decision.kind === 'vehicle' && duplicate === null)

  return {
    text: tooLong ? '' : text,
    kind: tooLong ? 'reject' : decision.kind,
    missing: role === 'admin' ? [] : decision.missing,
    plate,
    by: c.by,
    refused: false,
    tooLong,
    length: text.length,
    duplicate,
    canSend,
  }
}
