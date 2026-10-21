import { describe, expect, it } from 'vitest'
// O arquivo é o mesmo que o app do PC (C#) lê: shared/parser-cases.json, sem cópia.
import cases from '../../../shared/parser-cases.json'
import { parseAlert } from './parser'
import { extractPlate } from './plate'
import { alertText, decide } from './rules'
import type { Field, RuleKind, SenderRole } from './types'

interface ParserCase {
  raw: string
  role: SenderRole
  expectedText: string
  missing: Field[]
  kind: RuleKind
}

const parserCases = cases as ParserCase[]

describe('casos de shared/parser-cases.json', () => {
  it('o arquivo tem casos e só campos documentados', () => {
    expect(parserCases.length).toBeGreaterThan(80)
    for (const c of parserCases) {
      expect(Object.keys(c).sort()).toEqual(['expectedText', 'kind', 'missing', 'raw', 'role'])
      expect(['member', 'admin', 'operator']).toContain(c.role)
      expect(['vehicle', 'free', 'reject']).toContain(c.kind)
      expect(c.kind === 'reject').toBe(c.expectedText.length === 0)
    }
  })

  for (const { raw, role, expectedText, missing, kind } of parserCases) {
    it(`${role}: ${JSON.stringify(raw)}`, () => {
      const alert = parseAlert(raw)
      const decision = decide(alert, role)

      expect(decision.kind).toBe(kind)
      expect(decision.missing).toEqual(missing)
      expect(alertText(alert, decision)).toBe(expectedText)
    })
  }

  // A trava da IA compara a placa dela com a do texto: tem de ser a mesma que o parser acha.
  it('extractPlate acha a mesma placa que parseAlert em todos os casos', () => {
    for (const { raw } of parserCases) {
      expect(extractPlate(raw)?.plate ?? null, raw).toBe(parseAlert(raw).plate)
    }
  })
})
