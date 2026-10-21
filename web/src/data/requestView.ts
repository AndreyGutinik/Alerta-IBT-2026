import { upper } from '../alerts/format'
import type { LiveState, RequestRecord } from './types'
import { formatHm } from './time'

export type BadgeTone = 'q' | 'now' | 'ok' | 'no' | 'cx'
export type BadgeIcon = 'clock' | 'monitor-play' | 'check' | 'x' | 'ban' | 'hourglass'

export interface RequestBadge {
  tone: BadgeTone
  icon: BadgeIcon
  label: string
  /** O X de cancelar só aparece enquanto o aviso não começou a passar. */
  canCancel: boolean
  /** Texto riscado (cancelado e expirado), como o `.cx` do mockup. */
  struck: boolean
}

/** O selo de um pedido em "Seus avisos", a partir do `status` que o PC escreveu. */
export function requestBadge(r: RequestRecord, live: LiveState | null): RequestBadge {
  const status = r.status
  const state = status?.state ?? 'pending'

  // Pedido de cancelamento já gravado e o PC ainda não respondeu.
  if (r.cancel && (state === 'pending' || state === 'queued')) {
    return { tone: 'q', icon: 'clock', label: 'cancelando…', canCancel: false, struck: false }
  }

  switch (state) {
    case 'pending':
      return { tone: 'q', icon: 'clock', label: 'enviado', canCancel: true, struck: false }
    case 'queued': {
      const showPosition = status?.position !== undefined && live?.led !== false
      return {
        tone: 'q',
        icon: 'clock',
        label: showPosition ? `${status?.position}º na fila` : 'na fila',
        canCancel: true,
        struck: false,
      }
    }
    case 'showing':
      return { tone: 'now', icon: 'monitor-play', label: 'passando', canCancel: false, struck: false }
    case 'shown':
      return { tone: 'ok', icon: 'check', label: formatHm(status?.at ?? 0), canCancel: false, struck: false }
    case 'rejected':
      return { tone: 'no', icon: 'x', label: status?.reason || 'recusado', canCancel: false, struck: false }
    case 'cancelled':
      return {
        tone: 'cx',
        icon: 'ban',
        label: status?.reason ? `cancelado: ${status.reason}` : 'cancelado',
        canCancel: false,
        struck: true,
      }
    case 'expired':
      return { tone: 'cx', icon: 'hourglass', label: 'expirou', canCancel: false, struck: true }
  }
}

/** O texto do pedido: o final que o PC escreveu, senão a prévia, senão o texto digitado em MAIÚSCULAS. */
export function requestText(r: RequestRecord): string {
  return r.status?.text || r.preview?.text || upper(r.raw)
}
