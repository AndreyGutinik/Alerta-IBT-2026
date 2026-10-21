import {
  WS,
  isBlank,
  isWhiteSpaceCode,
  removePictographs,
  splitOnMasked,
  splitWords,
  trimEdges,
} from '../alerts/format'
import { extractPlate, normalizePlate } from '../alerts/plate'
import type { ParsedAlert } from '../alerts/types'
import { fold, isColor } from '../alerts/vocabulary'
import type { AiAlert, Correction } from './types'

// Porta da trava de segurança de CorrectionPipeline.cs e ColorLock.cs (app do PC): a IA só vale se não
// inventou placa, cor nem palavra. Qualquer dúvida e o resultado é o do parser local.

// Artigos e preposições que a IA pode acrescentar ou trocar na correção (sem acento, minúsculas).
const FUNCTION_WORDS: ReadonlySet<string> = new Set([
  'a', 'ao', 'aos', 'as', 'o', 'os', 'e', 'de', 'da', 'das', 'do', 'dos', 'em', 'na', 'nas', 'no', 'nos', 'um',
  'uma', 'para', 'pra', 'com', 'por',
])

// Palavras que invertem o sentido quando aparecem do nada: têm de estar no original, escritas igual.
const NEGATIONS: ReadonlySet<string> = new Set(['nao', 'nunca', 'sem', 'nem'])

// Feminino para masculino (sem acento, minúsculas): "preta" e "preto" são a mesma cor.
const MASCULINE: Readonly<Record<string, string>> = {
  preta: 'preto',
  branca: 'branco',
  vermelha: 'vermelho',
  amarela: 'amarelo',
  dourada: 'dourado',
  roxa: 'roxo',
}

const DIGIT_UNIT = /^\p{Nd}$/u

function hasDigit(word: string): boolean {
  for (let i = 0; i < word.length; i++) {
    if (DIGIT_UNIT.test(word[i])) return true
  }
  return false
}

/** Distância de edição (Levenshtein, por unidade UTF-16) entre duas palavras. */
export function editDistance(a: string, b: string): number {
  if (a.length === 0) return b.length
  if (b.length === 0) return a.length

  let previous = Array.from({ length: b.length + 1 }, (_, j) => j)
  let current = new Array<number>(b.length + 1).fill(0)
  for (let i = 1; i <= a.length; i++) {
    current[0] = i
    for (let j = 1; j <= b.length; j++) {
      const substitution = previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1)
      current[j] = Math.min(substitution, previous[j] + 1, current[j - 1] + 1)
    }
    ;[previous, current] = [current, previous]
  }
  return previous[b.length]
}

// ---- cor ----

function canonicalColor(word: string): string {
  const folded = fold(word)
  return MASCULINE[folded] ?? folded
}

function colorsIn(foldedWords: Iterable<string>): Set<string> {
  const colors = new Set<string>()
  for (const word of foldedWords) {
    if (isColor(word)) colors.add(canonicalColor(word))
  }
  return colors
}

/**
 * A cor da IA vale? Sem cor no texto original, qualquer uma (ou nenhuma) vale. Com cor no original, a IA
 * tem de dar cor, e só as que estão lá (preta = preto); sem cor na resposta só passa se não for aviso de
 * veículo.
 */
export function colorAllowed(rawColors: Set<string>, aiColor: string, vehicleAnswer: boolean): boolean {
  if (rawColors.size === 0) return true
  if (isBlank(aiColor)) return !vehicleAnswer
  const wanted = colorsIn(splitWords(aiColor).map(fold))
  if (wanted.size === 0) return false
  for (const color of wanted) {
    if (!rawColors.has(color)) return false
  }
  return true
}

// ---- palavras ----

// Onde o texto é cortado em palavras: espaço em branco, | / ; , (mas não vírgula entre dígitos) e hífen
// entre letras (como o WordSeparator do app do PC).
const WORD_SEPARATOR = new RegExp(
  `(?:${WS}|[|/;])+|(?<!\\p{Nd}),|,(?!\\p{Nd})|(?<=\\p{L})-(?=\\p{L})`,
  'gu',
)

function tokens(text: string): string[] {
  return splitOnMasked(text, WORD_SEPARATOR)
    .map(trimEdges)
    .filter((word) => word.length > 0)
}

/** Palavras em minúsculas e sem acento; a etiqueta "placa" não é palavra da mensagem. */
export function foldedWords(text: string): string[] {
  return tokens(text)
    .map(fold)
    .filter((word) => word.length > 0 && word !== 'placa')
}

/**
 * Uma palavra da IA é fiel quando o original já tem, quando é artigo ou preposição, ou quando é uma grafia
 * parecida com uma palavra do original: até 2 edições, até 1 se alguma das duas tem 3 letras ou menos
 * ("fox" não é erro de "gol"). Palavra com dígito ("hb20") e negação têm de ser iguais.
 */
export function isFaithful(word: string, rawWords: Set<string>): boolean {
  if (FUNCTION_WORDS.has(word) || rawWords.has(word)) return true
  if (NEGATIONS.has(word) || hasDigit(word)) return false

  for (const original of rawWords) {
    if (hasDigit(original)) continue
    const allowed = Math.min(word.length, original.length) <= 3 ? 1 : 2
    if (Math.abs(word.length - original.length) <= allowed && editDistance(word, original) <= allowed) {
      return true
    }
  }
  return false
}

// ---- placa ----

/**
 * O texto, sem espaço em branco e sem hífen, tem os 7 caracteres da placa em sequência? Os 7 têm de começar
 * onde uma palavra do texto começa: "aceso 1234" não esconde a placa "ESO-1234".
 */
export function hasPlateContiguously(text: string, plate: string): boolean {
  let compact = ''
  const wordStarts: number[] = []
  let atWordStart = true
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i)
    if (isWhiteSpaceCode(code)) {
      atWordStart = true
      continue
    }
    if (text[i] === '-') continue
    if (atWordStart) {
      wordStarts.push(compact.length)
      atWordStart = false
    }
    compact += text[i]
  }
  return wordStarts.some((start) => start + 7 <= compact.length && normalizePlate(compact.slice(start, start + 7)) === plate)
}

// ---- a resposta da IA como aviso ----

const REFUSAL: ParsedAlert = { model: '', color: '', plate: null, reason: '', freeText: '' }

function isRefusal(alert: ParsedAlert): boolean {
  return (
    isBlank(alert.freeText) && isBlank(alert.model) && isBlank(alert.color) && isBlank(alert.reason) && isBlank(alert.plate)
  )
}

/**
 * A resposta da IA como aviso, como o GeminiCorrector do PC lê: `free` com `texto` vazio é recusa; `free`
 * não tem veículo nem placa; `vehicle` leva a placa normalizada (ou nenhuma) e, sem `texto`, junta os
 * campos. `null` = nada aproveitável (nem recusa).
 */
export function readAiAlert(ai: AiAlert): ParsedAlert | null {
  const modelo = ai.modelo.trim()
  const cor = ai.cor.trim()
  const placa = ai.placa.trim()
  const motivo = ai.motivo.trim()
  const texto = ai.texto.trim()

  if (ai.kind === 'free') {
    return texto.length === 0 ? { ...REFUSAL } : { model: '', color: '', plate: null, reason: '', freeText: texto }
  }
  if (ai.kind === 'vehicle') {
    const plate = placa.length === 0 ? null : normalizePlate(placa)
    const freeText = texto.length > 0 ? texto : [modelo, cor, placa, motivo].filter((part) => part.length > 0).join(' ')
    // Nada foi lido do texto: não é resposta de confiança, e também não é recusa.
    return freeText.length === 0 ? null : { model: modelo, color: cor, plate, reason: motivo, freeText }
  }
  return null
}

/** O aviso da IA com a placa na forma de exibição se dá para confiar; senão `null`. */
function vet(raw: string, ai: ParsedAlert): ParsedAlert | null {
  const rawText = removePictographs(raw)
  const found = extractPlate(rawText)
  let expected = found ? found.plate : null
  const rawRest = found ? found.rest : rawText

  const plate = isBlank(ai.plate) ? null : normalizePlate(ai.plate ?? '')
  if (plate !== expected) {
    // Placa digitada com espaço ("abc 1234") não é achada no texto; a leitura da IA só vale se os sete
    // caracteres estiverem mesmo no texto, em sequência.
    if (expected !== null || plate === null || !hasPlateContiguously(rawText, plate)) return null
    expected = plate
  }

  const rawWords = new Set(foldedWords(rawRest))
  const vehicleAnswer = plate !== null || !isBlank(ai.model)
  if (!colorAllowed(colorsIn(rawWords), ai.color, vehicleAnswer)) return null

  for (const field of [ai.model, ai.color, ai.reason, ai.freeText]) {
    if (isBlank(field)) continue
    // Placa escrita dentro de qualquer campo também tem de ser a do original.
    const inField = extractPlate(removePictographs(field))
    if (inField && inField.plate !== expected) return null
    if (!foldedWords(inField ? inField.rest : removePictographs(field)).every((word) => isFaithful(word, rawWords))) {
      return null
    }
  }

  const freeText = isBlank(ai.freeText)
    ? [ai.model, ai.color, plate, ai.reason].filter((part) => !isBlank(part)).join(' ')
    : ai.freeText
  return { ...ai, plate, freeText }
}

/**
 * A trava de segurança (spec 5.3). `null` (IA não deu) e resposta que a trava recusa → parser local. A recusa
 * da IA (texto ofensivo) é vista antes da trava e vale: `refused`, sem cair no local.
 */
export function guardAiResult(raw: string, ai: AiAlert | null, local: ParsedAlert): Correction {
  const fallback: Correction = { alert: local, by: 'local', refused: false }
  if (ai === null) return fallback

  const answer = readAiAlert(ai)
  if (answer === null) return fallback
  if (isRefusal(answer)) return { alert: answer, by: 'gemini', refused: true }

  const trusted = vet(raw, answer)
  return trusted === null ? fallback : { alert: trusted, by: 'gemini', refused: false }
}
