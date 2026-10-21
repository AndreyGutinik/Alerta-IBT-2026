import type { ParsedAlert } from '../alerts/types'

/** A resposta estruturada da IA (o mesmo schema do Gemini do PC; ver `GeminiPrompt.cs`). */
export interface AiAlert {
  kind: 'vehicle' | 'free'
  modelo: string
  cor: string
  placa: string
  motivo: string
  texto: string
}

/**
 * A correção por IA. É só uma interface: o adaptador real (Firebase AI Logic) entra depois, os testes usam
 * um falso. `null` = não deu (erro, sem rede, resposta fora do formato): quem chama cai no parser local.
 * O `signal` é abortado quando o tempo máximo estoura.
 */
export interface AiCorrector {
  correct(raw: string, signal: AbortSignal): Promise<AiAlert | null>
}

/** Mesmo contrato, com o nome curto. */
export type Corrector = AiCorrector

/** Quem fez a correção: a IA (passou na trava) ou o parser local. */
export type CorrectedBy = 'gemini' | 'local'

export interface Correction {
  alert: ParsedAlert
  by: CorrectedBy
  /** A IA recusou o texto (ofensivo, trote): não vai para o telão. O PC decide de novo, com o filtro dele. */
  refused: boolean
}
