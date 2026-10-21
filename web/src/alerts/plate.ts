import { WS, isLetterOrDigitUnit, maskSurrogates, splitWords, trimWhiteSpace } from './format'

// Porta de PlateParser.cs (app do PC): o comportamento tem de ser o mesmo, caso a caso.

const PLATE_LENGTH = 7

function asLetter(c: string): string | null {
  if (c >= 'A' && c <= 'Z') return c
  switch (c) {
    case '0':
      return 'O'
    case '1':
      return 'I'
    case '5':
      return 'S'
    case '8':
      return 'B'
    default:
      return null
  }
}

function asDigit(c: string): string | null {
  if (c >= '0' && c <= '9') return c
  switch (c) {
    case 'O':
      return '0'
    case 'I':
      return '1'
    case 'S':
      return '5'
    case 'B':
      return '8'
    default:
      return null
  }
}

function isAsciiLetterOrDigit(c: string): boolean {
  return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
}

/**
 * Placa na forma de exibição (`ABC-1234` ou `ABC-1D23`), ou `null` se o texto não pode ser placa.
 *
 * Espaço, hífen e pontuação são ignorados; o que sobra tem de ter exatamente 7 letras e números. As
 * posições 1 a 3 são letras (0 1 5 8 valem O I S B), a 4, a 6 e a 7 são números (O I S B valem
 * 0 1 5 8) e a 5 fica como veio (letra na Mercosul, número na antiga). Letra ou dígito fora de A-Z
 * e 0-9 nunca é descartado: o texto "não é placa".
 */
export function normalizePlate(candidate: string): string | null {
  const typed: string[] = []
  // Por unidade UTF-16, como o foreach de char do app do PC.
  for (let i = 0; i < candidate.length; i++) {
    const c = candidate[i]
    if (isAsciiLetterOrDigit(c)) {
      if (typed.length === PLATE_LENGTH) return null
      typed.push(c.toUpperCase())
    } else if (isLetterOrDigitUnit(c)) {
      return null
    }
  }
  if (typed.length !== PLATE_LENGTH) return null

  const slots: string[] = []
  for (let i = 0; i < PLATE_LENGTH; i++) {
    const slot = i < 3 ? asLetter(typed[i]) : i === 4 ? typed[i] : asDigit(typed[i])
    if (slot === null) return null
    slots.push(slot)
  }
  return `${slots.slice(0, 3).join('')}-${slots.slice(3).join('')}`
}

// Três letras, separador opcional e [0-9OISB][0-9A-Za-z][0-9OISB]{2}, sem letra ou dígito colado, com
// a palavra "placa" (e ":" ou "-" opcional) engolida quando vem logo antes. O separador é
// (\s*-\s*|\s+)? e não \s*-?\s*: casa o mesmo texto sem recuar de forma quadrática numa fila de
// espaços. O fim da palavra "placa" é o \b do .NET (\w = L, Mn, Nd, Pc).
const CANDIDATE = new RegExp(
  '(?<![\\p{L}\\p{N}])' +
    '(?<label>[Pp][Ll][Aa][Cc][Aa](?![\\p{L}\\p{Mn}\\p{Nd}\\p{Pc}])' +
    WS +
    '*(?:[:\\-]' +
    WS +
    '*)?)?' +
    '(?<plate>[A-Za-z]{3}(?<separator>' +
    WS +
    '*-' +
    WS +
    '*|' +
    WS +
    '+)?[0-9OoIiSsBb][0-9A-Za-z][0-9OoIiSsBb]{2})' +
    '(?![\\p{L}\\p{N}])',
  'gu',
)

interface Candidate {
  index: number
  length: number
  plate: string
  isSpaced: boolean
}

// "Gol 2015" é uma palavra e um número, não uma placa: separada só por espaço, só vale se for Mercosul
// (letra na 5ª posição) ou se vier logo depois da palavra "placa".
function evaluate(match: RegExpExecArray): Candidate | null {
  const groups = match.groups as { label?: string; plate: string; separator?: string }
  const typed = groups.plate
  if (!/[0-9]/.test(typed)) return null
  const plate = normalizePlate(typed)
  if (plate === null) return null

  const separator = groups.separator ?? ''
  const isSpaced = separator.length > 0 && !separator.includes('-')
  if (isSpaced && groups.label === undefined && !/^[A-Za-z]$/.test(plate[5])) return null

  return { index: match.index, length: match[0].length, plate, isSpaced }
}

// A placa em que extractPlate e locatePlate concordam: a junta (abc1234, abc-1234, gbr- 9003) ganha da
// separada só por espaço, onde quer que cada uma esteja; entre iguais, a primeira.
function find(text: string): Candidate | null {
  let firstSpaced: Candidate | null = null
  // Casa na cópia mascarada (ver maskSurrogates); índices e tamanhos valem no texto original.
  for (const match of maskSurrogates(text).matchAll(CANDIDATE)) {
    const candidate = evaluate(match)
    if (candidate === null) continue
    if (!candidate.isSpaced) return candidate
    firstSpaced ??= candidate
  }
  return firstSpaced
}

const LINE_ENDINGS = /\r\n|[\n\r\f\u{85}\u{2028}\u{2029}]/gu

// Mantém as quebras de linha (como \n); em cada linha junta os espaços e apara; linhas vazias somem.
function tidyLines(text: string): string {
  return text
    .replace(LINE_ENDINGS, '\n')
    .split('\n')
    .map((line) => splitWords(trimWhiteSpace(line)).join(' '))
    .filter((line) => line.length > 0)
    .join('\n')
}

/**
 * Procura uma placa em texto livre, por exemplo `gol branco placa ffo4745 farol aceso`.
 *
 * `rest` é o texto sem a placa e sem a palavra "placa" que a rotula, com uma quebra de linha (`\n`) no
 * lugar dela: a placa é a fronteira mais confiável entre o veículo e o motivo. As quebras que já
 * estavam no texto ficam; em cada linha os espaços são juntados e a linha é aparada; linha vazia
 * some. Sem placa, devolve `null`.
 */
export function extractPlate(text: string): { plate: string; rest: string } | null {
  const found = find(text)
  if (found === null) return null
  const end = found.index + found.length
  return { plate: found.plate, rest: tidyLines(`${text.slice(0, found.index)}\n${text.slice(end)}`) }
}

/** Texto sem placa: o mesmo texto arrumado (como `rest` de `extractPlate`). */
export function tidyText(text: string): string {
  return tidyLines(text)
}

/** Onde fica a mesma placa de `extractPlate` (rótulo `placa:` incluído); `null` se não há. */
export function locatePlate(text: string): { index: number; length: number } | null {
  const found = find(text)
  return found === null ? null : { index: found.index, length: found.length }
}
