import { formatFree, formatVehicle } from './format'
import type { Field, ParsedAlert, Role, RuleDecision } from './types'

/** O que falta no aviso, sempre na ordem modelo, cor, placa. */
export function missingFields(a: ParsedAlert): Field[] {
  const missing: Field[] = []
  if (!a.model.trim()) missing.push('modelo')
  if (!a.color.trim()) missing.push('cor')
  if (!a.plate) missing.push('placa')
  return missing
}

/**
 * Regra da spec 5.2. Membro: precisa de modelo, cor e placa, senão recusa. Admin: com placa vira
 * aviso de veículo (mesmo faltando modelo ou cor), sem placa vira aviso livre.
 */
export function decide(a: ParsedAlert, role: Role): RuleDecision {
  const missing = missingFields(a)
  if (role === 'admin') return { kind: a.plate ? 'vehicle' : 'free', missing }
  return { kind: missing.length > 0 ? 'reject' : 'vehicle', missing }
}

/** Texto final em maiúsculas: aviso livre sem estrutura, o resto no formato do veículo. */
export function alertText(a: ParsedAlert, d: RuleDecision): string {
  return d.kind === 'free' ? formatFree(a.freeText) : formatVehicle(a)
}
