import type { ParsedAlert } from './types'

/** Tamanho máximo do texto digitado (`raw`), o mesmo das regras do banco. */
export const MAX_RAW = 300

// Letra ou dígito decimal, como o char.IsLetterOrDigit do app do PC.
const LETTER_OR_DIGIT = /[\p{L}\p{Nd}]/u

// O que a fonte do LED não desenha: símbolos "outros" e modificadores (emoji, tom de pele), a cola
// invisível das sequências de emoji (ZWJ e seletores de variação), a moldura de tecla (U+20E3, do
// "2" com moldura) e metade solta de par substituto.
const PICTOGRAPH = /[\p{So}\p{Sk}\p{Cs}‍︎️⃣]/gu

/** Junta espaços repetidos (quebras de linha também) e apara as pontas. */
export function normalizeSpaces(text: string): string {
  return text.replace(/\s+/g, ' ').trim()
}

/** Tira emoji e outros pictogramas, que a fonte do LED não desenha. */
export function removePictographs(text: string): string {
  return text.replace(PICTOGRAPH, '')
}

/** O texto tem ao menos uma letra ou um dígito? */
export function hasLetterOrDigit(text: string): boolean {
  return LETTER_OR_DIGIT.test(text)
}

/** Tira das duas pontas tudo o que não é letra nem dígito. */
export function trimToLettersAndDigits(text: string): string {
  const chars = Array.from(text)
  let start = 0
  let end = chars.length
  while (start < end && !LETTER_OR_DIGIT.test(chars[start])) start++
  while (end > start && !LETTER_OR_DIGIT.test(chars[end - 1])) end--
  return chars.slice(start, end).join('')
}

/** Texto final do telão: sem pictogramas, espaços arrumados e tudo em MAIÚSCULAS. */
export function upper(text: string): string {
  return normalizeSpaces(removePictographs(text)).toLocaleUpperCase('pt-BR')
}

/** `MODELO COR | PLACA: ABC-1234 | MOTIVO`; a parte que falta some junto com o separador. */
export function formatVehicle(a: ParsedAlert): string {
  const plate = upper(a.plate ?? '')
  const parts = [upper(`${a.model} ${a.color}`), plate ? `PLACA: ${plate}` : '', upper(a.reason)]
  return parts.filter(Boolean).join(' | ')
}

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
