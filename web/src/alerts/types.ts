/** Campo que o aviso de veículo precisa ter. */
export type Field = 'modelo' | 'cor' | 'placa'

export type Role = 'member' | 'admin'

/** O aviso lido do texto digitado (ou corrigido pela IA). */
export interface ParsedAlert {
  model: string
  color: string
  plate: string | null
  reason: string
  /** O texto todo como aviso livre: sem o # do começo e numa linha só. */
  freeText: string
}

export type RuleKind = 'vehicle' | 'free' | 'reject'

export interface RuleDecision {
  kind: RuleKind
  missing: Field[]
}
