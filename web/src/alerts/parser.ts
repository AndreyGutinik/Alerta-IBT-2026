import {
  formatFree,
  hasLetterOrDigit,
  removePictographs,
  splitOnMasked,
  splitWords,
  trimEdges,
  trimStartToLetterOrDigit,
  trimWhiteSpace,
  upper,
} from './format'
import { extractPlate, locatePlate, tidyText } from './plate'
import type { ParsedAlert } from './types'
import { fold, isColor, isColorModifier, isReasonStart } from './vocabulary'

// Porta de AlertParser.cs (app do PC): o comportamento tem de ser o mesmo, caso a caso.

// Onde o texto é cortado em segmentos: quebra de linha, | / ; e vírgula, menos a vírgula com dígito dos
// dois lados (1,0). A placa também deixa uma quebra de linha para trás (extractPlate).
const SEGMENT_SEPARATOR = /[\n|/;]|(?<!\p{Nd}),|,(?!\p{Nd})/gu

// As palavras de cada segmento, em ordem; segmento sem palavra fica de fora.
function readSegments(rest: string): string[][] {
  const segments: string[][] = []
  for (const segment of splitOnMasked(rest, SEGMENT_SEPARATOR)) {
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
function takeVehicle(segments: string[][]): { vehicle: string[]; next: number; vehicleIndex: number } {
  const index = segments.findIndex((words) => !isReasonStart(words[0]))
  if (index < 0) return { vehicle: [], next: segments.length, vehicleIndex: -1 }

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
  return { vehicle: vehicle.map(trimEdges).filter((word) => fold(word) !== 'placa'), next: index, vehicleIndex: index }
}

// Tira a palavra de cor em `start` (e o modificador logo depois, se houver) e devolve as duas, sem a
// pontuação das pontas.
function take(words: string[], start: number): string {
  const count = start + 1 < words.length && isColorModifier(words[start + 1]) ? 2 : 1
  const taken = words.slice(start, start + count).map(trimEdges).join(' ')
  words.splice(start, count)
  return taken
}

// O mais de palavras que cabem entre a placa e a cor quando são o fim do modelo ("fiat abc1234 uno preto").
const MAX_MODEL_WORDS_AFTER_PLATE = 2

// Quantas palavras do segmento vêm antes da cor dele, quando o segmento não abre com gatilho e tem uma
// palavra de cor nas primeiras MAX_MODEL_WORDS_AFTER_PLATE palavras, sem gatilho antes; 0 nos outros casos
// (e quando a cor é a primeira palavra).
function leadBeforeColor(segment: string[]): number {
  if (isReasonStart(segment[0])) return 0
  for (let i = 1; i <= MAX_MODEL_WORDS_AFTER_PLATE && i < segment.length; i++) {
    if (isReasonStart(segment[i])) return 0
    if (isColor(segment[i])) return i
  }
  return 0
}

// A cor é a primeira palavra de cor do veículo; senão, a palavra que abre o segmento logo depois dele
// (antes dela, logo depois da placa, até duas palavras que não são gatilho vão para o modelo). O
// modificador que vem depois da cor vai junto. As duas saem de onde estavam.
function takeColor(vehicle: string[], reasons: string[][], next: number, plateFollowsVehicle: boolean): string {
  const at = vehicle.findIndex(isColor)
  if (at >= 0) return take(vehicle, at)

  const lead = next < reasons.length && plateFollowsVehicle ? leadBeforeColor(reasons[next]) : 0
  if (lead > 0) {
    vehicle.push(
      ...reasons[next]
        .slice(0, lead)
        .map(trimEdges)
        .filter((word) => fold(word) !== 'placa'),
    )
    reasons[next].splice(0, lead)
  }

  if (next < reasons.length && isColor(reasons[next][0])) {
    const color = take(reasons[next], 0)
    if (reasons[next].length === 0) reasons.splice(next, 1)
    else trimSegmentStart(reasons[next])
    return color
  }
  return ''
}

function isColorOnly(words: string[]): boolean {
  return isColor(words[0]) && (words.length === 1 || (words.length === 2 && isColorModifier(words[1])))
}

function hasColorBeforeTrigger(words: string[]): boolean {
  for (const word of words) {
    if (isReasonStart(word)) return false
    if (isColor(word)) return true
  }
  return false
}

// Uma cor sozinha antes do modelo ("Prata, Gol, ABC-1234"): o primeiro segmento que não é motivo é só uma
// cor (com o modificador) e o segmento logo depois é o começo de um veículo (não é gatilho, não tem cor
// própria antes do primeiro gatilho), do mesmo lado da placa. O segmento da cor sai e a cor é a do veículo.
function takeLeadingColorSegment(segments: string[][], plateBreak: { value: number }): string | null {
  const first = segments.findIndex((words) => !isReasonStart(words[0]))
  if (first < 0 || first + 1 >= segments.length || !isColorOnly(segments[first])) return null

  const model = segments[first + 1]
  const crossesPlate = first < plateBreak.value && plateBreak.value <= first + 1
  if (crossesPlate || isReasonStart(model[0]) || isColorOnly(model) || hasColorBeforeTrigger(model)) return null

  const color = segments[first].map(trimEdges).join(' ')
  segments.splice(first, 1)
  if (plateBreak.value > first) plateBreak.value--
  return color
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

  // Quantos segmentos ficam antes da placa (ela deixou uma quebra de linha ali): as regras de aviso fora
  // de ordem tratam "depois da placa" diferente de "depois de uma vírgula". -1 sem placa.
  const located = found ? locatePlate(text) : null
  const plateBreak = { value: located ? readSegments(text.slice(0, located.index)).length : -1 }

  const leadingColor = takeLeadingColorSegment(segments, plateBreak)
  const { vehicle, next, vehicleIndex } = takeVehicle(segments)
  // O veículo já saiu dos segmentos: o que sobra neles são os motivos.
  const color = leadingColor ?? takeColor(vehicle, segments, next, plateBreak.value === vehicleIndex + 1)

  return {
    model: upper(vehicle.join(' ')),
    color: upper(color),
    plate: found ? found.plate : null,
    reason: upper(segments.map((words) => words.join(' ')).join(', ')),
    freeText: formatFree(text),
  }
}
