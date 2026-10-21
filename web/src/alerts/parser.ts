import { normalizeSpaces } from './format'
import { findPlate, prepareText, stripHash } from './plate'
import type { ParsedAlert } from './types'
import { isColor, isReasonStart } from './vocabulary'

/**
 * Lê o texto digitado e separa modelo, cor, placa e motivo (spec 5.2). Também guarda o texto todo
 * como `freeText`, para o aviso livre do admin.
 */
export function parseAlert(raw: string): ParsedAlert {
  const text = prepareText(raw)
  const hit = findPlate(text)
  // A placa vira um separador: o que veio antes e o que veio depois ficam em segmentos diferentes.
  const marked = hit ? `${hit.before} | ${hit.after}` : text
  const segments = marked
    .split(/[|/;]+/)
    .map((segment) => segment.trim())
    .filter(Boolean)

  const { vehicle, reason } = splitVehicle(segments)
  return {
    model: vehicle.filter((word) => !isColor(word)).join(' '),
    color: vehicle.filter((word) => isColor(word)).join(' '),
    plate: hit?.plate ?? null,
    reason,
    freeText: normalizeSpaces(stripHash(raw)),
  }
}

/**
 * O primeiro segmento que não começa com palavra de motivo é o veículo, até a primeira palavra de
 * motivo. O resto desse segmento e os demais segmentos formam o motivo, juntados com espaço.
 */
function splitVehicle(segments: string[]): { vehicle: string[]; reason: string } {
  const wordsOf = (segment: string) => segment.split(/\s+/)
  const vehicleAt = segments.findIndex((segment) => !isReasonStart(wordsOf(segment)[0]))
  if (vehicleAt === -1) return { vehicle: [], reason: segments.join(' ') }

  const words = wordsOf(segments[vehicleAt])
  const reasonAt = words.findIndex((word) => isReasonStart(word))
  const hasReasonWords = reasonAt !== -1
  return {
    vehicle: hasReasonWords ? words.slice(0, reasonAt) : words,
    reason: [
      ...segments.slice(0, vehicleAt),
      ...(hasReasonWords ? [words.slice(reasonAt).join(' ')] : []),
      ...segments.slice(vehicleAt + 1),
    ].join(' '),
  }
}
