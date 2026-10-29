import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { AI_SCHEMA, DEFAULT_MODEL, SYSTEM_PROMPT, parseAiJson, thinkingConfigFor } from './prompt'

describe('parseAiJson', () => {
  it('JSON válido vira o objeto, com os campos aparados', () => {
    expect(
      parseAiJson('{"kind":"vehicle","modelo":" Gol ","cor":"branco","placa":"FFO4745","motivo":"farol aceso","texto":"Gol branco"}'),
    ).toEqual({ kind: 'vehicle', modelo: 'Gol', cor: 'branco', placa: 'FFO4745', motivo: 'farol aceso', texto: 'Gol branco' })
    expect(parseAiJson('{"kind":"free","texto":"Aviso"}')).toEqual({
      kind: 'free',
      modelo: '',
      cor: '',
      placa: '',
      motivo: '',
      texto: 'Aviso',
    })
  })

  it('JSON inválido ou vazio vira null', () => {
    expect(parseAiJson('não é json')).toBeNull()
    expect(parseAiJson('')).toBeNull()
    expect(parseAiJson('{"kind":"vehicle"')).toBeNull()
  })

  it('kind desconhecido ou ausente vira null', () => {
    expect(parseAiJson('{"kind":"other","texto":"x"}')).toBeNull()
    expect(parseAiJson('{"texto":"x"}')).toBeNull()
  })

  it('campo que falta ou não é texto vira vazio', () => {
    expect(parseAiJson('{"kind":"vehicle","modelo":"Gol","cor":7,"placa":null}')).toEqual({
      kind: 'vehicle',
      modelo: 'Gol',
      cor: '',
      placa: '',
      motivo: '',
      texto: '',
    })
  })

  it('array, número, texto e null vêm como null', () => {
    expect(parseAiJson('[]')).toBeNull()
    expect(parseAiJson('[{"kind":"free"}]')).toBeNull()
    expect(parseAiJson('42')).toBeNull()
    expect(parseAiJson('"free"')).toBeNull()
    expect(parseAiJson('null')).toBeNull()
  })
})

describe('thinkingConfigFor (igual ao CreateThinkingConfig do PC)', () => {
  it('Gemini 2.5: orçamento 0; Gemini 3: nível mínimo; outros: nada', () => {
    expect(thinkingConfigFor('gemini-2.5-flash')).toEqual({ thinkingBudget: 0 })
    expect(thinkingConfigFor('Gemini-3.5-flash-lite')).toEqual({ thinkingLevel: 'MINIMAL' })
    expect(thinkingConfigFor('gemini-2.0-flash')).toBeUndefined()
    expect(thinkingConfigFor('outro-modelo')).toBeUndefined()
  })
})

// Paridade com o PC (spec 5.1: o celular e o PC fazem o mesmo pedido). Só roda quando o código C# está no
// repositório; fora dele (site sozinho) o teste é pulado.
const PROMPT_CS = resolve(import.meta.dirname, '../../../desktop/src/AlertaIBT.Core/Correction/GeminiPrompt.cs')
const hasCs = existsSync(PROMPT_CS)

function csUnescape(literal: string): string {
  return literal.replace(/\\(?:U([0-9A-Fa-f]{8})|(["\\n]))/g, (_all, hex?: string, simple?: string) => {
    if (hex) return String.fromCodePoint(parseInt(hex, 16))
    return simple === 'n' ? '\n' : (simple as string)
  })
}

describe.skipIf(!hasCs)('paridade com GeminiPrompt.cs', () => {
  const source = hasCs ? readFileSync(PROMPT_CS, 'utf8') : ''

  it('a instrução de sistema é a mesma, letra por letra', () => {
    const block = /public const string System =([\s\S]*?);\s*\n/.exec(source)?.[1] ?? ''
    const literals = [...block.matchAll(/"((?:[^"\\\n]|\\.)*)"/g)].map((m) => csUnescape(m[1]))

    expect(literals.length).toBeGreaterThan(5)
    expect(SYSTEM_PROMPT).toBe(literals.join(''))
  })

  it('o modelo padrão é o mesmo', () => {
    expect(/DefaultModel = "([^"]+)"/.exec(source)?.[1]).toBe(DEFAULT_MODEL)
  })

  it('o schema tem os mesmos campos, todos obrigatórios, e o mesmo enum de kind', () => {
    const fields = /foreach \(var field in new\[\] \{([^}]*)\}/.exec(source)?.[1] ?? ''
    const names = [...fields.matchAll(/"(\w+)"/g)].map((m) => m[1])
    const required = /\["required"\] = new JsonArray\(([^)]*)\)/.exec(source)?.[1] ?? ''
    const kinds = /\["enum"\] = new JsonArray\(([^)]*)\)/.exec(source)?.[1] ?? ''

    expect(Object.keys(AI_SCHEMA.properties)).toEqual(['kind', ...names])
    expect([...required.matchAll(/"(\w+)"/g)].map((m) => m[1])).toEqual([...AI_SCHEMA.required])
    expect([...kinds.matchAll(/"(\w+)"/g)].map((m) => m[1])).toEqual([...AI_SCHEMA.properties.kind.enum])
  })
})

describe('SYSTEM_PROMPT', () => {
  it('diz que palavras comuns de carro e do dia a dia são normais e só recusa ódio, sexo explícito ou ameaça', () => {
    for (const phrase of ['bomba de gasolina', 'bomba de combustível', 'pau de selfie', 'pica-pau', 'pau-brasil', 'pinto (pintinho ou sobrenome)']) {
      expect(SYSTEM_PROMPT).toContain(phrase)
    }
    expect(SYSTEM_PROMPT).toContain('NORMAIS (não são ofensivas) e devem ser corrigidas, nunca recusadas')
    expect(SYSTEM_PROMPT).toContain('Só se o texto for mesmo de ódio, sexual explícito ou ameaça, use kind "free" com texto vazio.')
    expect(SYSTEM_PROMPT).toContain('Na dúvida, corrija.')
    expect(SYSTEM_PROMPT).not.toContain('trote')
  })


  it('pede para não inventar, manter a placa e usar texto vazio para conteúdo impróprio', () => {
    expect(SYSTEM_PROMPT).toContain('Nunca invente palavras, modelos, cores ou placas. Mantenha a placa exatamente como veio.')
    expect(SYSTEM_PROMPT).toContain('use kind "free" com texto vazio')
    expect(SYSTEM_PROMPT).toContain('"video aberto" quer dizer "vidro aberto"')
  })

  it('manda reordenar avisos fora de ordem e continua curta (mesmo teto do PC: 300 a 2000)', () => {
    expect(SYSTEM_PROMPT).toContain('Os campos podem vir fora de ordem.')
    expect(SYSTEM_PROMPT).toContain('Prata, Gol, ABC-1234 = modelo Gol, cor prata.')
    expect(SYSTEM_PROMPT.length).toBeGreaterThanOrEqual(300)
    expect(SYSTEM_PROMPT.length).toBeLessThanOrEqual(2000)
  })
})
