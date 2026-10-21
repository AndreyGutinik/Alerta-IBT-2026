import { normalizeSpaces } from './format'

const TO_LETTER: Record<string, string> = { '0': 'O', '1': 'I', '5': 'S', '8': 'B' }
const TO_DIGIT: Record<string, string> = { O: '0', I: '1', S: '5', B: '8' }

/**
 * Placa no formato `LLL-NXNN`: 7 letras e números depois de tirar espaço, hífen e pontuação.
 * Posições 0 a 2 são letras (0→O, 1→I, 5→S, 8→B), 3, 5 e 6 são números (O→0, I→1, S→5, B→8)
 * e a 4 fica como veio (letra = Mercosul, número = antiga). Devolve null se não for placa.
 */
export function normalizePlate(candidate: string): string | null {
  const chars = candidate.replace(/[^a-z0-9]/gi, '').toUpperCase()
  if (chars.length !== 7) return null

  const letters = [...chars.slice(0, 3)].map((c) => TO_LETTER[c] ?? c).join('')
  const digit = TO_DIGIT[chars[3]] ?? chars[3]
  const fifth = chars[4]
  const lastDigits = [...chars.slice(5)].map((c) => TO_DIGIT[c] ?? c).join('')

  if (!/^[A-Z]{3}$/.test(letters) || !/^\d$/.test(digit) || !/^\d{2}$/.test(lastDigits)) return null
  return `${letters}-${digit}${fifth}${lastDigits}`
}

const LEADING_HASH = /^\s*#+\s*/
const PLATE_WORD = /\bplaca\b\s*:?\s*/gi
// 3 letras e 4 caracteres (o 1º e os 2 últimos podem ser letra parecida com número), com espaço
// ou hífen opcional no meio, entre separadores ou nas pontas do texto.
const CANDIDATE = /(^|[\s|/;,.:])([a-z]{3}\s*-?\s*[0-9oisb][0-9a-z][0-9oisb]{2})(?=$|[\s|/;,.:!?])/gi

/** Tira o `#` do começo do texto (e o espaço que vem depois dele). */
export function stripHash(raw: string): string {
  return raw.replace(LEADING_HASH, '')
}

/**
 * Passos 1 e 2 do parser (spec 5.2): sem o `#` do começo, quebras de linha como ` | `
 * e sem a palavra `placa` (com `:` opcional).
 */
export function prepareText(raw: string): string {
  return stripHash(raw).replace(/\r?\n/g, ' | ').replace(PLATE_WORD, ' ')
}

export interface PlateHit {
  plate: string
  /** O texto antes da placa. */
  before: string
  /** O texto depois da placa. */
  after: string
}

/** Passo 3 do parser: a primeira placa válida de um texto já preparado por `prepareText`. */
export function findPlate(text: string): PlateHit | null {
  for (const match of text.matchAll(CANDIDATE)) {
    const plate = normalizePlate(match[2])
    if (plate) {
      const start = match.index + match[1].length
      return { plate, before: text.slice(0, start), after: text.slice(start + match[2].length) }
    }
  }
  return null
}

/**
 * Acha a primeira placa do texto, do mesmo jeito que `parseAlert`. `rest` é o texto sem a placa e
 * sem a palavra `placa`, com os espaços juntados (as quebras de linha aparecem como ` | `).
 */
export function extractPlate(text: string): { plate: string; rest: string } | null {
  const hit = findPlate(prepareText(text))
  if (!hit) return null
  return { plate: hit.plate, rest: normalizeSpaces(`${hit.before} ${hit.after}`) }
}
