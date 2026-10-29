import type { AiAlert } from './types'

// O pedido que o celular faz à IA é o mesmo que o PC faz (spec 5.1): o texto abaixo, o schema e o modelo
// padrão seguem desktop/src/AlertaIBT.Core/Correction/GeminiPrompt.cs. O teste prompt.test.ts compara com o
// arquivo C# quando ele está no repositório.

/**
 * O modelo padrão. Conferido em https://ai.google.dev/gemini-api/docs/pricing em 2026-10-20: o Flash-Lite
 * estável com plano grátis.
 */
export const DEFAULT_MODEL = 'gemini-3.5-flash-lite'

/**
 * A instrução de sistema, em pt-BR e curta (custo e latência). Pede só correção de grafia, os campos do
 * aviso de veículo e `texto` vazio para conteúdo impróprio, que a trava lê como recusa.
 */
export const SYSTEM_PROMPT =
  'Você corrige avisos curtos de estacionamento de uma igreja, mostrados num painel de LED.\n' +
  'Regras:\n' +
  '- Corrija ortografia, acentos e concordância. Não traduza.\n' +
  '- Nunca invente palavras, modelos, cores ou placas. Mantenha a placa exatamente como veio.\n' +
  '- Extraia modelo (do carro), cor, placa e motivo (pode ficar vazio).\n' +
  '- Se o texto não for sobre um veículo, use kind "free" e coloque o texto corrigido em texto.\n' +
  '- Palavras comuns de carro e do dia a dia são NORMAIS (não são ofensivas) e devem ser corrigidas, nunca recusadas: ' +
  'bomba de gasolina, bomba de combustível, pau de selfie, pica-pau, pau-brasil, pinto (pintinho ou sobrenome).\n' +
  '- Só se o texto for mesmo de ódio, sexual explícito ou ameaça, use kind "free" com texto vazio. ' +
  'Na dúvida, corrija.\n' +
  '- Sempre preencha texto com o aviso inteiro corrigido.\n' +
  '- Em aviso de estacionamento, "video aberto" quer dizer "vidro aberto" (idem fechado, quebrado).\n' +
  '- Os campos podem vir fora de ordem. Reordene sempre para modelo, cor, placa, motivo, em cada campo e em texto, ' +
  'sem perder nem inventar nenhuma palavra.\n' +
  'Fora de ordem: fiat abc1234 uno preto = modelo Fiat Uno, cor preto. Prata, Gol, ABC-1234 = modelo Gol, cor prata. ' +
  'gol farol aceso branco abc1234 = modelo Gol, cor branco, motivo farol aceso. ' +
  'a placa ABC1234 gol branco esta com farol aceso = modelo Gol, cor branco, motivo esta com farol aceso.\n' +
  'Exemplo 1: corola prata abc1d23 vidro abrto\n' +
  '{"kind":"vehicle","modelo":"Corolla","cor":"Prata","placa":"ABC1D23","motivo":"vidro aberto",' +
  '"texto":"Corolla prata ABC1D23 vidro aberto"}\n' +
  'Exemplo 2: pais do pedro compareser na recepssao\n' +
  '{"kind":"free","modelo":"","cor":"","placa":"","motivo":"",' +
  '"texto":"Pais do Pedro comparecer na recepção"}\n' +
  'Exemplo 3: fox prata abc1d54 video aberto\n' +
  '{"kind":"vehicle","modelo":"Fox","cor":"Prata","placa":"ABC1D54","motivo":"vidro aberto",' +
  '"texto":"Fox prata ABC1D54 vidro aberto"}'

/** Os campos de texto da resposta, além de `kind`. */
export const AI_TEXT_FIELDS = ['modelo', 'cor', 'placa', 'motivo', 'texto'] as const

/**
 * O schema da resposta (JSON Schema): `kind` (`vehicle` ou `free`), `modelo`, `cor`, `placa`, `motivo` e
 * `texto`, todos texto e todos obrigatórios, como o `CreateSchema` do PC.
 */
export const AI_SCHEMA = {
  type: 'object',
  properties: {
    kind: { type: 'string', enum: ['vehicle', 'free'] },
    modelo: { type: 'string' },
    cor: { type: 'string' },
    placa: { type: 'string' },
    motivo: { type: 'string' },
    texto: { type: 'string' },
  },
  required: ['kind', 'modelo', 'cor', 'placa', 'motivo', 'texto'],
} as const

/**
 * O `thinkingConfig` que faz o modelo pensar o mínimo, para a resposta caber no tempo máximo:
 * `thinkingLevel: 'MINIMAL'` no Gemini 3 em diante (os mais antigos recusam) e `thinkingBudget: 0` no
 * Gemini 2.5. Outro modelo fica sem configuração (alguns recusam). Igual ao `CreateThinkingConfig` do PC.
 */
export function thinkingConfigFor(model: string): { thinkingBudget: number } | { thinkingLevel: 'MINIMAL' } | undefined {
  const name = model.toLowerCase()
  if (name.startsWith('gemini-2.5')) return { thinkingBudget: 0 }
  if (name.startsWith('gemini-3')) return { thinkingLevel: 'MINIMAL' }
  return undefined
}

function text(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

/**
 * A resposta da IA em texto JSON → `AiAlert`. JSON inválido, que não é objeto ou com `kind` fora de
 * `vehicle`/`free` → `null` (quem chama cai no parser local). Campo que falta ou não é texto vira `''`.
 */
export function parseAiJson(json: string): AiAlert | null {
  let value: unknown
  try {
    value = JSON.parse(json)
  } catch {
    return null
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null

  const record = value as Record<string, unknown>
  const kind = record.kind
  if (kind !== 'vehicle' && kind !== 'free') return null
  return {
    kind,
    modelo: text(record.modelo),
    cor: text(record.cor),
    placa: text(record.placa),
    motivo: text(record.motivo),
    texto: text(record.texto),
  }
}
