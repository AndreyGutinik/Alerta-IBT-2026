import { formatFree, formatVehicle, isWhiteSpaceCode } from './format'
import type { Field, ParsedAlert, RuleDecision, RuleKind, SenderRole } from './types'

/**
 * O máximo de caracteres de um aviso de membro depois de limpo e formatado (o texto que chegaria à
 * barra). Admin e operador não têm limite. É a mesma regra do app do PC (AlertRules.MemberMaxLength).
 */
export const MEMBER_MAX_LENGTH = 200

/**
 * O texto formatado é maior do que o papel pode mandar? Só admin e operador são ilimitados; papel
 * desconhecido tem o limite do membro.
 */
export function isTooLong(formattedText: string, role: SenderRole): boolean {
  return role !== 'admin' && role !== 'operator' && formattedText.length > MEMBER_MAX_LENGTH
}

function isBlank(text: string | null): boolean {
  if (!text) return true
  for (let i = 0; i < text.length; i++) {
    if (!isWhiteSpaceCode(text.charCodeAt(i))) return false
  }
  return true
}

/** O que falta no aviso, sempre na ordem modelo, cor, placa. */
export function missingFields(a: ParsedAlert): Field[] {
  const missing: Field[] = []
  if (isBlank(a.model)) missing.push('modelo')
  if (isBlank(a.color)) missing.push('cor')
  if (isBlank(a.plate)) missing.push('placa')
  return missing
}

function kindFor(a: ParsedAlert, role: SenderRole, missing: Field[]): RuleKind {
  // Nada para mostrar (sem texto, só # e emoji): recusado para todo mundo.
  if (isBlank(a.freeText)) return 'reject'
  if (role === 'admin' || role === 'operator') return isBlank(a.plate) ? 'free' : 'vehicle'
  return missing.length === 0 ? 'vehicle' : 'reject'
}

/**
 * Regra da spec 5.2. Membro: precisa de modelo, cor e placa, senão recusa. Admin e operador: com
 * placa vira aviso de veículo (mesmo faltando modelo ou cor), sem placa vira aviso livre.
 */
export function decide(a: ParsedAlert, role: SenderRole): RuleDecision {
  const missing = missingFields(a)
  return { kind: kindFor(a, role, missing), missing }
}

/** Texto final em MAIÚSCULAS: aviso livre sem estrutura, veículo no formato; recusa não tem texto. */
export function alertText(a: ParsedAlert, d: RuleDecision): string {
  if (d.kind === 'reject') return ''
  return d.kind === 'free' ? formatFree(a.freeText) : formatVehicle(a)
}
