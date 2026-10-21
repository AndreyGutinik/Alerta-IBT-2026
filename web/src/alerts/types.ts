/** Campo que o aviso de veículo precisa ter. */
export type Field = 'modelo' | 'cor' | 'placa'

/** Papel de uma pessoa do site (`/users/{uid}/role`). */
export type Role = 'member' | 'admin'

/** Quem manda o aviso: o papel da pessoa ou o operador do PC (mesmas regras do admin). */
export type SenderRole = Role | 'operator'

/** O aviso lido do texto digitado (ou corrigido pela IA). */
export interface ParsedAlert {
  model: string
  color: string
  plate: string | null
  reason: string
  /** O texto todo como aviso livre: sem emoji, sem o # do começo, numa linha só, em MAIÚSCULAS. */
  freeText: string
}

export type RuleKind = 'vehicle' | 'free' | 'reject'

export interface RuleDecision {
  kind: RuleKind
  missing: Field[]
}
