import type { Role } from '../alerts/types'
import type { LiveBar, LiveItem, LiveNow, LiveRecent, LiveSource, LiveState, UserRecord } from './types'

/** Sem heartbeat há mais que isto (pela hora do servidor) = "PC offline". */
export const PC_OFFLINE_MS = 90_000
/** Mesma placa na fila, passando ou exibida nesta janela = repetido. */
export const DUPLICATE_WINDOW_MS = 600_000
/** Membro: 1 aviso a cada 20 s (as regras do banco garantem). */
export const RATE_LIMIT_MS = 20_000

const DEFAULT_BAR: LiveBar = { position: 'top', height: 56, speed: 90 }
const SOURCES: readonly string[] = ['telegram', 'site', 'pc']
const RECENT_STATES: readonly string[] = ['shown', 'rejected', 'cancelled', 'expired']

type Obj = Record<string, unknown>

function isObj(value: unknown): value is Obj {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function num(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function positive(value: unknown): number | undefined {
  const n = num(value)
  return n !== undefined && n > 0 ? n : undefined
}

function str(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined
}

/** O RTDB devolve lista como array, ou como objeto de chaves numéricas quando há buracos. */
function toList(value: unknown): Obj[] {
  let items: unknown[]
  if (Array.isArray(value)) {
    items = value
  } else if (isObj(value)) {
    items = Object.entries(value)
      .sort(([a], [b]) => {
        const na = Number(a)
        const nb = Number(b)
        if (Number.isInteger(na) && Number.isInteger(nb)) return na - nb
        return a < b ? -1 : a > b ? 1 : 0
      })
      .map(([, item]) => item)
  } else {
    return []
  }
  return items.filter(isObj)
}

function source(value: unknown): LiveSource {
  return typeof value === 'string' && SOURCES.includes(value) ? (value as LiveSource) : 'pc'
}

function withPlate<T extends { plate?: string }>(item: T, raw: Obj): T {
  const plate = str(raw.plate)
  if (plate) item.plate = plate
  return item
}

function normalizeItem(raw: Obj): LiveItem {
  const item: LiveItem = { id: String(raw.id ?? ''), text: str(raw.text) ?? '', source: source(raw.source) }
  if (raw.urgent === true) item.urgent = true
  return withPlate(item, raw)
}

function normalizeNow(value: unknown): LiveNow | null {
  if (!isObj(value) || typeof value.text !== 'string') return null
  const now: LiveNow = {
    id: String(value.id ?? ''),
    text: value.text,
    source: source(value.source),
    startedAt: num(value.startedAt) ?? 0,
  }
  const durationMs = positive(value.durationMs)
  if (durationMs !== undefined) now.durationMs = durationMs
  return withPlate(now, value)
}

function normalizeRecent(raw: Obj): LiveRecent | null {
  if (typeof raw.state !== 'string' || !RECENT_STATES.includes(raw.state)) return null
  const recent: LiveRecent = {
    id: String(raw.id ?? ''),
    text: str(raw.text) ?? '',
    state: raw.state as LiveRecent['state'],
    at: num(raw.at) ?? 0,
  }
  return withPlate(recent, raw)
}

function normalizeBar(value: unknown): LiveBar {
  if (!isObj(value)) return { ...DEFAULT_BAR }
  const bar: LiveBar = {
    position: value.position === 'bottom' ? 'bottom' : 'top',
    height: positive(value.height) ?? DEFAULT_BAR.height,
    speed: positive(value.speed) ?? DEFAULT_BAR.speed,
  }
  const fontSize = positive(value.fontSize)
  if (fontSize !== undefined) bar.fontSize = fontSize
  return bar
}

/** `/live` como veio do banco → o estado que as telas usam (listas viram arrays, `bar` ganha padrão). */
export function normalizeLive(value: unknown): LiveState | null {
  if (!isObj(value)) return null
  const live: LiveState = {
    heartbeatAt: num(value.heartbeatAt) ?? 0,
    led: value.led === true,
    paused: value.paused === true,
    now: normalizeNow(value.now),
    queue: toList(value.queue).map(normalizeItem),
    recent: toList(value.recent)
      .map(normalizeRecent)
      .filter((r): r is LiveRecent => r !== null),
    bar: normalizeBar(value.bar),
  }
  const version = str(value.version)
  if (version !== undefined) live.version = version
  const gemini = str(value.gemini)
  if (gemini !== undefined) live.gemini = gemini
  return live
}

/** O PC está vivo? Sempre pela hora do servidor; sem `/live` ou sem heartbeat = não. */
export function isPcOnline(live: LiveState | null, serverNow: number): boolean {
  return live !== null && serverNow - live.heartbeatAt <= PC_OFFLINE_MS
}

const PLATE_IN_TEXT = /PLACA:\s*([A-Z]{3}-[0-9][A-Z0-9][0-9]{2})/i

/** A placa de um texto de aviso (`... | PLACA: ABC-1234 | ...`), ou `null` (aviso livre). */
export function plateFromText(text: string): string | null {
  const match = PLATE_IN_TEXT.exec(text)
  return match ? match[1].toUpperCase() : null
}

export interface Duplicate {
  where: 'queue' | 'now' | 'recent'
  /** O texto do aviso antes do primeiro ` | ` (modelo e cor). */
  label: string
  minutesAgo?: number
}

function labelOf(text: string): string {
  const cut = text.indexOf(' | ')
  return cut < 0 ? text : text.slice(0, cut)
}

function plateOf(item: { plate?: string; text: string }): string | null {
  return item.plate ? item.plate.toUpperCase() : plateFromText(item.text)
}

/**
 * A mesma placa já está na fila, passando ou foi exibida nos últimos 10 min? Recusado, cancelado e
 * expirado não contam. Sem placa (aviso livre) nunca é repetido.
 */
export function findDuplicate(plate: string | null, live: LiveState | null, serverNow: number): Duplicate | null {
  if (!plate || !live) return null
  const wanted = plate.toUpperCase()

  const inQueue = live.queue.find((item) => plateOf(item) === wanted)
  if (inQueue) return { where: 'queue', label: labelOf(inQueue.text) }

  if (live.now && plateOf(live.now) === wanted) return { where: 'now', label: labelOf(live.now.text) }

  let latest: LiveRecent | null = null
  for (const r of live.recent) {
    if (r.state !== 'shown' || plateOf(r) !== wanted) continue
    if (serverNow - r.at > DUPLICATE_WINDOW_MS) continue
    if (latest === null || r.at > latest.at) latest = r
  }
  if (latest) {
    const minutesAgo = Math.max(1, Math.round((serverNow - latest.at) / 60000))
    return { where: 'recent', label: labelOf(latest.text), minutesAgo }
  }
  return null
}

export type Tone = 'ok' | 'warn' | 'bad'

/** A pílula do cabeçalho. Prioridade: sem conexão, PC offline, barra pausada, telão desligado, ligado. */
export function headerPill(s: { connected: boolean; live: LiveState | null; serverNow: number }): {
  tone: Tone
  label: string
} {
  if (!s.connected) return { tone: 'bad', label: 'Sem conexão' }
  if (!isPcOnline(s.live, s.serverNow) || s.live === null) return { tone: 'bad', label: 'PC offline' }
  if (s.live.paused) return { tone: 'warn', label: 'Barra pausada' }
  if (!s.live.led) return { tone: 'bad', label: 'Telão desligado' }
  return { tone: 'ok', label: 'Telão ligado' }
}

/** Segundos até a pessoa poder mandar de novo (arredondado para cima). Admin e sem registro: 0. */
export function rateLimitLeft(record: UserRecord | null, role: Role, serverNow: number): number {
  if (role === 'admin' || record === null || record.lastSentAt === undefined) return 0
  const leftMs = record.lastSentAt + RATE_LIMIT_MS - serverNow
  return leftMs > 0 ? Math.ceil(leftMs / 1000) : 0
}

/** A pílula do cabeçalho da aba Status: sem conexão (do celular), PC offline ou PC online. */
export function pcPill(s: { connected: boolean; live: LiveState | null; serverNow: number }): {
  tone: Tone
  label: string
} {
  if (!s.connected) return { tone: 'bad', label: 'Sem conexão' }
  if (!isPcOnline(s.live, s.serverNow)) return { tone: 'bad', label: 'PC offline' }
  return { tone: 'ok', label: 'PC online' }
}
