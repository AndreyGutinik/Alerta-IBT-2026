import {
  type Database,
  equalTo,
  get,
  onValue,
  orderByChild,
  push,
  query,
  ref,
  set,
  update,
} from 'firebase/database'
import type { Role } from '../alerts/types'
import type { Backend, Unsubscribe } from '../data/backend'
import { normalizeLive } from '../data/live'
import type { LiveState, RequestRecord, SendInput, SessionUser, UserRecord, WithId } from '../data/types'
import { buildSeenUpdate, buildSendUpdate, buildUserRecord } from '../data/writes'
import { normalizeRequest, normalizeUser } from './normalize'
import { OfflineError, RequestSender } from './sender'

function isPermissionDenied(e: unknown): boolean {
  if (typeof e !== 'object' || e === null) return false
  const { code, message } = e as { code?: unknown; message?: unknown }
  return [code, message].some((v) => typeof v === 'string' && v.toLowerCase().includes('permission_denied'))
}

/**
 * O `Backend` sobre o Realtime Database. Escritas só saem com conexão: o SDK guarda em silêncio o que é
 * gravado sem rede e o faria depois, então sem conexão (`.info/connected`) tudo falha na hora com `OfflineError`.
 */
export class FirebaseBackend implements Backend {
  private connected = false
  private offsetMs = 0
  private readonly sender: RequestSender
  private readonly stops: Unsubscribe[] = []

  constructor(private readonly db: Database) {
    this.stops.push(
      onValue(ref(db, '.info/connected'), (s) => {
        this.connected = s.val() === true
      }),
      onValue(ref(db, '.info/serverTimeOffset'), (s) => {
        const value: unknown = s.val()
        this.offsetMs = typeof value === 'number' && Number.isFinite(value) ? value : 0
      }),
    )
    this.sender = new RequestSender({
      isConnected: () => this.connected,
      newKey: () => {
        const key = push(ref(this.db, 'requests')).key
        if (!key) throw new Error('Não deu para gerar o id do pedido.')
        return key
      },
      write: (id, input) => update(ref(this.db), buildSendUpdate(id, input)),
    })
  }

  /** Para os dois ouvintes internos (os testes usam; o app deixa ligados). */
  dispose(): void {
    for (const stop of this.stops.splice(0)) stop()
  }

  isConnected(): boolean {
    return this.connected
  }

  serverNow(): number {
    return Date.now() + this.offsetMs
  }

  private requireOnline(): void {
    if (!this.connected) throw new OfflineError()
  }

  async ensureUser(u: SessionUser): Promise<void> {
    const mine = ref(this.db, `users/${u.uid}`)
    try {
      const snap = await get(mine)
      if (!snap.exists()) {
        await set(mine, buildUserRecord(u))
      } else if (snap.child('blocked').val() !== true) {
        await update(mine, buildSeenUpdate(u))
      }
    } catch (e) {
      // Duas abas criando o registro ao mesmo tempo: a segunda é recusada e o registro já existe.
      if (!isPermissionDenied(e)) throw e
    }
  }

  watchUser(uid: string, cb: (r: UserRecord | null) => void): Unsubscribe {
    return onValue(
      ref(this.db, `users/${uid}`),
      (s) => cb(normalizeUser(s.val())),
      () => cb(null),
    )
  }

  watchLive(cb: (l: LiveState | null) => void): Unsubscribe {
    return onValue(
      ref(this.db, 'live'),
      (s) => cb(normalizeLive(s.val())),
      () => cb(null),
    )
  }

  watchMyRequests(uid: string, cb: (list: WithId<RequestRecord>[]) => void): Unsubscribe {
    const mine = query(ref(this.db, 'requests'), orderByChild('uid'), equalTo(uid))
    return onValue(
      mine,
      (s) => {
        const list: WithId<RequestRecord>[] = []
        s.forEach((child) => {
          const request = normalizeRequest(child.val())
          if (request && child.key) list.push({ ...request, id: child.key })
        })
        cb(list)
      },
      () => cb([]),
    )
  }

  watchUsers(cb: (list: WithId<UserRecord>[]) => void): Unsubscribe {
    return onValue(
      ref(this.db, 'users'),
      (s) => {
        const list: WithId<UserRecord>[] = []
        s.forEach((child) => {
          const record = normalizeUser(child.val())
          if (record && child.key) list.push({ ...record, id: child.key })
        })
        cb(list)
      },
      () => cb([]),
    )
  }

  watchConnected(cb: (online: boolean) => void): Unsubscribe {
    return onValue(ref(this.db, '.info/connected'), (s) => cb(s.val() === true))
  }

  send(input: SendInput): Promise<string> {
    return this.sender.send(input)
  }

  async cancel(id: string): Promise<void> {
    this.requireOnline()
    await set(ref(this.db, `requests/${id}/cancel`), true)
  }

  async setRole(uid: string, role: Role): Promise<void> {
    this.requireOnline()
    await set(ref(this.db, `users/${uid}/role`), role)
  }

  async setBlocked(uid: string, blocked: boolean): Promise<void> {
    this.requireOnline()
    await set(ref(this.db, `users/${uid}/blocked`), blocked)
  }
}
