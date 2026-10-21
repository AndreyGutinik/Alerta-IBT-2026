import type { RequestPreview, RequestRecord, RequestState, RequestStatus, UserRecord } from '../data/types'

type Obj = Record<string, unknown>

function isObj(value: unknown): value is Obj {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
const str = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined)
const num = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined)

const STATES: readonly string[] = ['pending', 'queued', 'showing', 'shown', 'rejected', 'cancelled', 'expired']

/** `/users/{uid}` como veio do banco → o registro das telas. Qualquer coisa que não seja um objeto vira `null`. */
export function normalizeUser(value: unknown): UserRecord | null {
  if (!isObj(value)) return null
  const record: UserRecord = {
    name: str(value.name) ?? '',
    email: str(value.email) ?? '',
    role: value.role === 'admin' ? 'admin' : 'member',
    blocked: value.blocked === true,
    createdAt: num(value.createdAt) ?? 0,
  }
  const lastSeenAt = num(value.lastSeenAt)
  if (lastSeenAt !== undefined) record.lastSeenAt = lastSeenAt
  const lastSentAt = num(value.lastSentAt)
  if (lastSentAt !== undefined) record.lastSentAt = lastSentAt
  const lastRequestId = str(value.lastRequestId)
  if (lastRequestId !== undefined) record.lastRequestId = lastRequestId
  const sentCount = num(value.sentCount)
  if (sentCount !== undefined) record.sentCount = sentCount
  return record
}

function normalizePreview(value: unknown): RequestPreview | undefined {
  if (!isObj(value) || typeof value.text !== 'string') return undefined
  const preview: RequestPreview = {
    text: value.text,
    kind: value.kind === 'free' ? 'free' : 'vehicle',
    by: value.by === 'gemini' ? 'gemini' : 'local',
  }
  const plate = str(value.plate)
  if (plate) preview.plate = plate
  return preview
}

function normalizeStatus(value: unknown): RequestStatus | undefined {
  if (!isObj(value) || typeof value.state !== 'string' || !STATES.includes(value.state)) return undefined
  const status: RequestStatus = { state: value.state as RequestState, at: num(value.at) ?? 0 }
  const position = num(value.position)
  if (position !== undefined) status.position = position
  const reason = str(value.reason)
  if (reason !== undefined) status.reason = reason
  const text = str(value.text)
  if (text !== undefined) status.text = text
  return status
}

/** `/requests/{id}` → pedido das telas; sem `raw` de texto não há o que mostrar (`null`). */
export function normalizeRequest(value: unknown): RequestRecord | null {
  if (!isObj(value) || typeof value.raw !== 'string') return null
  const request: RequestRecord = {
    uid: str(value.uid) ?? '',
    name: str(value.name) ?? '',
    raw: value.raw,
    urgent: value.urgent === true,
    createdAt: num(value.createdAt) ?? 0,
    cancel: value.cancel === true,
  }
  const preview = normalizePreview(value.preview)
  if (preview) request.preview = preview
  const status = normalizeStatus(value.status)
  if (status) request.status = status
  return request
}
