import {
  formatFree,
  hasLetterOrDigit,
  maskSurrogates,
  removePictographs,
  splitWords,
  trimEdges,
  trimStartToLetterOrDigit,
  trimWhiteSpace,
  upper,
} from './format'
import { extractPlate, tidyText } from './plate'
import type { ParsedAlert } from './types'
import { fold, isColor, isColorModifier, isReasonStart } from './vocabulary'

// Porta de AlertParser.cs (app do PC): o comportamento tem de ser o mesmo, caso a caso.

// Onde o texto é cortado em segmentos: quebra de linha, | / ; e vírgula, menos a vírgula com dígito dos
// dois lados (1,0). A placa também deixa uma quebra de linha para trás (extractPlate).
const SEGMENT_SEPARATOR = /[\n|/;]|(?<!\p{Nd}),|,(?!\p{Nd})/gu

// As palavras de cada segmento, em ordem; segmento sem palavra fica de fora.
function readSegments(rest: string): string[][] {
  const segments: string[][] = []
  // Corta o original onde a cópia mascarada (maskSurrogates) tem separador.
  const pieces: string[] = []
  let from = 0
  for (const cut of maskSurrogates(rest).matchAll(SEGMENT_SEPARATOR)) {
    pieces.push(rest.slice(from, cut.index))
    from = cut.index + cut[0].length
  }
  pieces.push(rest.slice(from))

  for (const segment of pieces) {
    const words = splitWords(trimEdges(segment)).filter(hasLetterOrDigit)
    if (words.length > 0) segments.push(words)
  }
  return segments
}

// Um segmento cortado no começo (o motivo que abre numa palavra-gatilho dentro do veículo, o que sobra
// de um motivo depois da cor) é aparado ali como qualquer outro. O fim já estava aparado e a pontuação
// entre as palavras fica.
function trimSegmentStart(words: string[]): string[] {
  words[0] = trimStartToLetterOrDigit(words[0])
  return words
}

// Tira o veículo dos segmentos e devolve as palavras dele (aparadas nas pontas, sem "placa"): o primeiro
// segmento que não começa com gatilho, cortado no primeiro gatilho dentro dele. O que sobra em
// `segments` são os motivos, em ordem; `next` é onde está o segmento logo depois do veículo.
function takeVehicle(segments: string[][]): { vehicle: string[]; next: number } {
  const index = segments.findIndex((words) => !isReasonStart(words[0]))
  if (index < 0) return { vehicle: [], next: segments.length }

  const vehicle = segments[index]
  let trigger = -1
  for (let i = 1; i < vehicle.length; i++) {
    if (isReasonStart(vehicle[i])) {
      trigger = i
      break
    }
  }
  if (trigger >= 0) {
    segments.splice(index + 1, 0, trimSegmentStart(vehicle.slice(trigger)))
    vehicle.length = trigger
  }

  segments.splice(index, 1)
  return { vehicle: vehicle.map(trimEdges).filter((word) => fold(word) !== 'placa'), next: index }
}

// Tira a palavra de cor em `start` (e o modificador logo depois, se houver) e devolve as duas, sem a
// pontuação das pontas.
function take(words: string[], start: number): string {
  const count = start + 1 < words.length && isColorModifier(words[start + 1]) ? 2 : 1
  const taken = words.slice(start, start + count).map(trimEdges).join(' ')
  words.splice(start, count)
  return taken
}

// A cor é a primeira palavra de cor do veículo; senão, a palavra que abre o segmento logo depois dele.
// O modificador que vem depois da cor vai junto. As duas saem de onde estavam.
function takeColor(vehicle: string[], reasons: string[][], next: number): string {
  const at = vehicle.findIndex(isColor)
  if (at >= 0) return take(vehicle, at)

  if (next < reasons.length && isColor(reasons[next][0])) {
    const color = take(reasons[next], 0)
    if (reasons[next].length === 0) reasons.splice(next, 1)
    else trimSegmentStart(reasons[next])
    return color
  }
  return ''
}

/**
 * Lê o que a pessoa digitou (`#creta preto | placa:gbr- 9003 / farol aceso`) em modelo, cor, placa e
 * motivo. Determinístico e sem dicionário: ortografia é trabalho da correção.
 *
 * Emoji saem, o `#` do começo sai e tudo vira MAIÚSCULAS (pt-BR); nada é cortado. `freeText` é sempre o
 * texto todo limpo. Veja os comentários de AlertParser.cs para as regras de segmento, veículo, cor e
 * motivo (motivos são juntados com ", ").
 */
export function parseAlert(raw: string): ParsedAlert {
  // Trim() do .NET e depois TrimStart('#'): só o # sai, sem aparar o que vem depois dele.
  const text = trimWhiteSpace(removePictographs(raw)).replace(/^#+/, '')
  const found = extractPlate(text)
  const rest = found ? found.rest : tidyText(text)

  const segments = readSegments(rest)
  const { vehicle, next } = takeVehicle(segments)
  // O veículo já saiu dos segmentos: o que sobra neles são os motivos.
  const color = takeColor(vehicle, segments, next)

  return {
    model: upper(vehicle.join(' ')),
    color: upper(color),
    plate: found ? found.plate : null,
    reason: upper(segments.map((words) => words.join(' ')).join(', ')),
    freeText: formatFree(text),
  }
}
