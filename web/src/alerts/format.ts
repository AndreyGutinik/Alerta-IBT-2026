import type { ParsedAlert } from './types'

/** Tamanho máximo do texto digitado (`raw`), o mesmo das regras do banco. */
export const MAX_RAW = 300

// Espaço em branco como o char.IsWhiteSpace do .NET (e o \s do Regex dele): categorias Z, mais
// \t \n \v \f \r e o NEL (U+0085). O \s do JavaScript difere (inclui U+FEFF, não inclui U+0085).
const WHITESPACE = new Set<number>([
  0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
])

/** Espaço em branco do .NET, para uma unidade UTF-16. */
export function isWhiteSpaceCode(code: number): boolean {
  return WHITESPACE.has(code) || (code >= 0x2000 && code <= 0x200a)
}

/**
 * Trecho de regex (flag `u`) que casa um espaço em branco, igual ao do .NET. Usa a forma com chaves
 * porque a camada de escrita de arquivos converte `\u` minúsculo sem chaves em caractere cru.
 */
export const WS =
  '[\\t\\n\\v\\f\\r \\x85\\xa0\\u{1680}\\u{2000}-\\u{200a}\\u{2028}\\u{2029}\\u{202f}\\u{205f}\\u{3000}]'

const LETTER_OR_DIGIT_UNIT = /^[\p{L}\p{Nd}]$/u

// O que a fonte do LED não desenha: símbolos "outros" e modificadores (emoji, tom de pele), a moldura
// de tecla (Me, que cobre U+20E3), a cola invisível das sequências de emoji (ZWJ e seletores de
// variação) e metade solta de par substituto (o .NET também a descarta).
const PICTOGRAPH = /[\p{So}\p{Sk}\p{Me}\p{Cs}\u{200d}\u{fe0e}\u{fe0f}]/gu

/**
 * Letra ou dígito decimal numa unidade UTF-16, como o char.IsLetterOrDigit do app do PC: metade de
 * par substituto nunca é letra.
 */
export function isLetterOrDigitUnit(unit: string): boolean {
  return LETTER_OR_DIGIT_UNIT.test(unit)
}

/** Alguma unidade do texto é letra ou dígito (como `text.Any(char.IsLetterOrDigit)`)? */
export function hasLetterOrDigit(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    if (isLetterOrDigitUnit(text[i])) return true
  }
  return false
}

// Sem a flag `u`, a classe casa unidade por unidade, inclusive as duas metades de um par válido.
const SURROGATE_UNIT = new RegExp(`[${String.fromCharCode(0xd800)}-${String.fromCharCode(0xdfff)}]`, 'g')

/**
 * Cópia do texto, com o mesmo tamanho, em que toda unidade UTF-16 de par substituto vira U+E000
 * (uso privado: nem letra, nem dígito, nem espaço). O .NET olha o texto por unidade UTF-16, onde
 * metade de par substituto nunca é letra nem dígito; já as regex do JavaScript com a flag `u` olham
 * por ponto de código (uma letra matemática fora do BMP seria letra). Casar nesta cópia dá o
 * resultado do .NET, e os índices valem no texto original.
 */
export function maskSurrogates(text: string): string {
  return text.replace(SURROGATE_UNIT, '\u{e000}')
}

/** Tira o espaço em branco das duas pontas (como `string.Trim()`). */
export function trimWhiteSpace(text: string): string {
  let start = 0
  let end = text.length
  while (start < end && isWhiteSpaceCode(text.charCodeAt(start))) start++
  while (end > start && isWhiteSpaceCode(text.charCodeAt(end - 1))) end--
  return text.slice(start, end)
}

/** Palavras separadas por espaço em branco, sem vazias. */
export function splitWords(text: string): string[] {
  const words: string[] = []
  let start = -1
  for (let i = 0; i < text.length; i++) {
    if (isWhiteSpaceCode(text.charCodeAt(i))) {
      if (start >= 0) words.push(text.slice(start, i))
      start = -1
    } else if (start < 0) {
      start = i
    }
  }
  if (start >= 0) words.push(text.slice(start))
  return words
}

/** Junta espaços repetidos (quebras de linha também) e apara as pontas. */
export function normalizeSpaces(text: string): string {
  return splitWords(text).join(' ')
}

/** Tira emoji e outros pictogramas, que a fonte do LED não desenha. */
export function removePictographs(text: string): string {
  return text.replace(PICTOGRAPH, '')
}

/** Tira do começo tudo o que não é letra nem dígito. */
export function trimStartToLetterOrDigit(text: string): string {
  let start = 0
  while (start < text.length && !isLetterOrDigitUnit(text[start])) start++
  return text.slice(start)
}

/** Tira do fim tudo o que não é letra nem dígito. */
export function trimEndToLetterOrDigit(text: string): string {
  let end = text.length
  while (end > 0 && !isLetterOrDigitUnit(text[end - 1])) end--
  return text.slice(0, end)
}

/** Tira das duas pontas tudo o que não é letra nem dígito. */
export function trimEdges(text: string): string {
  return trimEndToLetterOrDigit(trimStartToLetterOrDigit(text))
}

/** Sem pictogramas, numa linha só, aparado e em MAIÚSCULAS (pt-BR); vazio para `null`. */
export function upper(text: string | null | undefined): string {
  if (!text) return ''
  return normalizeSpaces(removePictographs(text)).toLocaleUpperCase('pt-BR')
}

/** `MODELO COR | PLACA: ABC-1234 | MOTIVO`; a parte que falta some junto com o separador. */
export function formatVehicle(a: ParsedAlert): string {
  const plate = upper(a.plate)
  const parts = [upper(`${a.model} ${a.color}`), plate ? `PLACA: ${plate}` : '', upper(a.reason)]
  return parts.filter(Boolean).join(' | ')
}

/** Aviso livre: sem pictogramas, uma linha, MAIÚSCULAS; nada é cortado. */
export function formatFree(text: string): string {
  return upper(text)
}

/** Corta em MAX_RAW sem deixar meio emoji (par substituto cortado) na ponta. */
function cut(text: string): string {
  if (text.length <= MAX_RAW) return text
  const lastKept = text.charCodeAt(MAX_RAW - 1)
  const splitsPair = lastKept >= 0xd800 && lastKept <= 0xdbff
  return text.slice(0, splitsPair ? MAX_RAW - 1 : MAX_RAW)
}

/**
 * Valor do campo a cada tecla: uma linha só e no máximo 300 caracteres.
 * Não apara, senão a pessoa não conseguiria digitar o espaço entre as palavras.
 */
export function fieldValue(input: string): string {
  return cut(input.replace(/\r?\n/g, ' '))
}

/** Texto que vai no pedido (`raw`): espaços juntados, aparado e no máximo 300 caracteres. */
export function cleanRaw(input: string): string {
  // O corte pode cair logo depois de um espaço.
  return cut(normalizeSpaces(input)).trimEnd()
}
