import { describe, expect, it } from 'vitest'
import cases from './parser-cases.json'
import { parseAlert } from './parser'
import { extractPlate } from './plate'
import { alertText, decide } from './rules'
import type { Field, Role, RuleKind } from './types'

// Mesmo formato previsto para o shared/parser-cases.json, que o app do PC (C#) também vai ler.
interface ParserCase {
  raw: string
  role: Role
  expectedText: string
  missing: Field[]
  kind: RuleKind
}

const parserCases = cases as ParserCase[]

describe('casos do parser-cases.json', () => {
  for (const { raw, role, expectedText, missing, kind } of parserCases) {
    it(`${role}: ${JSON.stringify(raw)}`, () => {
      const alert = parseAlert(raw)
      const decision = decide(alert, role)

      expect(decision.kind).toBe(kind)
      expect(decision.missing).toEqual(missing)
      expect(alertText(alert, decision)).toBe(expectedText)
    })
  }

  // A trava da IA compara a placa dela com extractPlate(raw): tem de ser a mesma que o parser acha.
  it('extractPlate acha a mesma placa que parseAlert em todos os casos', () => {
    for (const { raw } of parserCases) {
      expect(extractPlate(raw)?.plate ?? null, raw).toBe(parseAlert(raw).plate)
    }
  })
})
