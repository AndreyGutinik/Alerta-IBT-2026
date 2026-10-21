import type { Role } from '../alerts/types'

// Contrato do Realtime Database (spec 9.2 e "Contrato com o PC" do plano). Horários em ms, hora do
// servidor. Quem escreve cada campo está nas regras (database.rules.json).

/** `/users/{uid}`: o registro de cada pessoa que já entrou. */
export interface UserRecord {
  name: string
  email: string
  role: Role
  blocked: boolean
  createdAt: number
  lastSeenAt?: number
  lastSentAt?: number
  /** Id do último pedido: amarra um pedido por envio para o limite de 20 s não ser furado. */
  lastRequestId?: string
  sentCount?: number
}

/** Pedido novo vai sem `status` (= pendente). Só a conta do PC escreve `status`. */
export type RequestState = 'pending' | 'queued' | 'showing' | 'shown' | 'rejected' | 'cancelled' | 'expired'

export interface RequestPreview {
  text: string
  kind: 'vehicle' | 'free'
  plate?: string
  by: 'gemini' | 'local'
}

export interface RequestStatus {
  state: RequestState
  /** Começa em 1. */
  position?: number
  /** Texto curto em pt-BR, pronto para mostrar (ex.: `faltou placa`, `repetido`, `pregação`). */
  reason?: string
  text?: string
  at: number
}

/** `/requests/{id}`. */
export interface RequestRecord {
  uid: string
  name: string
  raw: string
  urgent: boolean
  createdAt: number
  preview?: RequestPreview
  cancel: boolean
  status?: RequestStatus
}

export type LiveSource = 'telegram' | 'site' | 'pc'

/** Item da fila; para item do site, `id` é o id do pedido (admin cancela gravando `cancel = true`). */
export interface LiveItem {
  id: string
  text: string
  source: LiveSource
  urgent?: boolean
  plate?: string
}

export interface LiveNow {
  id: string
  text: string
  source: LiveSource
  startedAt: number
  plate?: string
}

export interface LiveRecent {
  id: string
  text: string
  state: 'shown' | 'rejected' | 'cancelled' | 'expired'
  at: number
  plate?: string
}

export interface LiveBar {
  position: 'top' | 'bottom'
  height: number
  /** Sem `fontSize` vale 64% da altura. */
  fontSize?: number
  speed: number
}

/** `/live`: o que o PC publica a cada mudança e num heartbeat. */
export interface LiveState {
  heartbeatAt: number
  version?: string
  led: boolean
  paused: boolean
  gemini?: string
  now: LiveNow | null
  queue: LiveItem[]
  recent: LiveRecent[]
  bar: LiveBar
}

export interface SessionUser {
  uid: string
  name: string
  email: string
}

export interface SendInput {
  uid: string
  name: string
  raw: string
  urgent: boolean
  preview: RequestPreview | null
}

export type WithId<T> = T & { id: string }
