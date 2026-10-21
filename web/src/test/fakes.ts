import { act } from '@testing-library/react'
import type { Role } from '../alerts/types'
import type { AuthPort, Backend, Unsubscribe } from '../data/backend'
import type { LiveState, RequestRecord, SendInput, SessionUser, UserRecord, WithId } from '../data/types'

// Falsos de AuthPort e Backend para os testes das telas. Guardam as chamadas em arrays e têm `emit*` para
// empurrar dados como o banco faria. Quem assina depois de um `emit*` recebe o último valor (como o RTDB).
// Os `emit*` rodam dentro de `act` para o React não reclamar.

function emitInAct(fn: () => void): void {
  act(() => {
    fn()
  })
}

class Channel<T> {
  private listeners = new Set<(v: T) => void>()
  private has = false
  private last: T | undefined

  constructor(initial?: { value: T }) {
    if (initial) {
      this.has = true
      this.last = initial.value
    }
  }

  subscribe(cb: (v: T) => void): Unsubscribe {
    this.listeners.add(cb)
    if (this.has) cb(this.last as T)
    return () => {
      this.listeners.delete(cb)
    }
  }

  emit(value: T): void {
    this.has = true
    this.last = value
    emitInAct(() => {
      for (const cb of [...this.listeners]) cb(value)
    })
  }

  get count(): number {
    return this.listeners.size
  }
}

export class FakeAuth implements AuthPort {
  signInCalls = 0
  signOutCalls = 0
  signInImpl: () => Promise<void> = () => Promise.resolve()
  private channel: Channel<SessionUser | null>

  /** `initial`: o usuário que o Firebase já devolve ao abrir (`null` = sem login); omitido = ainda não respondeu. */
  constructor(initial?: SessionUser | null) {
    this.channel = new Channel(initial === undefined ? undefined : { value: initial })
  }

  onUserChanged(cb: (u: SessionUser | null) => void): Unsubscribe {
    return this.channel.subscribe(cb)
  }

  signIn(): Promise<void> {
    this.signInCalls += 1
    return this.signInImpl()
  }

  signOut(): Promise<void> {
    this.signOutCalls += 1
    return Promise.resolve()
  }

  emitUser(u: SessionUser | null): void {
    this.channel.emit(u)
  }
}

export class FakeBackend implements Backend {
  ensured: SessionUser[] = []
  sent: SendInput[] = []
  cancelled: string[] = []
  roleChanges: { uid: string; role: Role }[] = []
  blockChanges: { uid: string; blocked: boolean }[] = []
  /** Hora do servidor devolvida por `serverNow()`; os testes mexem nela. */
  now = 1_800_000_000_000
  ensureUserImpl: (u: SessionUser) => Promise<void> = () => Promise.resolve()
  sendImpl: (input: SendInput) => Promise<string> = () => Promise.resolve(`req-${this.sent.length}`)
  cancelImpl: (id: string) => Promise<void> = () => Promise.resolve()
  setRoleImpl: (uid: string, role: Role) => Promise<void> = () => Promise.resolve()
  setBlockedImpl: (uid: string, blocked: boolean) => Promise<void> = () => Promise.resolve()

  private user = new Channel<UserRecord | null>()
  private live = new Channel<LiveState | null>()
  private requests = new Channel<WithId<RequestRecord>[]>()
  private users = new Channel<WithId<UserRecord>[]>()
  private connected = new Channel<boolean>({ value: true })

  ensureUser(u: SessionUser): Promise<void> {
    this.ensured.push(u)
    return this.ensureUserImpl(u)
  }
  watchUser(_uid: string, cb: (r: UserRecord | null) => void): Unsubscribe {
    return this.user.subscribe(cb)
  }
  watchLive(cb: (l: LiveState | null) => void): Unsubscribe {
    return this.live.subscribe(cb)
  }
  watchMyRequests(_uid: string, cb: (list: WithId<RequestRecord>[]) => void): Unsubscribe {
    return this.requests.subscribe(cb)
  }
  watchUsers(cb: (list: WithId<UserRecord>[]) => void): Unsubscribe {
    return this.users.subscribe(cb)
  }
  watchConnected(cb: (online: boolean) => void): Unsubscribe {
    return this.connected.subscribe(cb)
  }
  serverNow(): number {
    return this.now
  }
  send(input: SendInput): Promise<string> {
    this.sent.push(input)
    return this.sendImpl(input)
  }
  cancel(id: string): Promise<void> {
    this.cancelled.push(id)
    return this.cancelImpl(id)
  }
  setRole(uid: string, role: Role): Promise<void> {
    this.roleChanges.push({ uid, role })
    return this.setRoleImpl(uid, role)
  }
  setBlocked(uid: string, blocked: boolean): Promise<void> {
    this.blockChanges.push({ uid, blocked })
    return this.setBlockedImpl(uid, blocked)
  }

  emitUser(r: UserRecord | null): void {
    this.user.emit(r)
  }
  emitLive(l: LiveState | null): void {
    this.live.emit(l)
  }
  emitRequests(list: WithId<RequestRecord>[]): void {
    this.requests.emit(list)
  }
  emitUsers(list: WithId<UserRecord>[]): void {
    this.users.emit(list)
  }
  setConnected(online: boolean): void {
    this.connected.emit(online)
  }

  /** Quantas telas ainda escutam (para testar que o desmonte cancela as assinaturas). */
  get listenerCount(): number {
    return (
      this.user.count + this.live.count + this.requests.count + this.users.count + this.connected.count
    )
  }
}

export const TEST_USER: SessionUser = { uid: 'u1', name: 'Joana Silva', email: 'joana@example.com' }

export function userRecord(over: Partial<UserRecord> = {}): UserRecord {
  return {
    name: TEST_USER.name,
    email: TEST_USER.email,
    role: 'member',
    blocked: false,
    createdAt: 1_700_000_000_000,
    ...over,
  }
}

/** Um `/live` saudável: heartbeat agora, telão ligado, fila e recentes vazios. */
export function liveState(over: Partial<LiveState> = {}, now = 1_800_000_000_000): LiveState {
  return {
    heartbeatAt: now,
    led: true,
    paused: false,
    now: null,
    queue: [],
    recent: [],
    bar: { position: 'top', height: 56, speed: 90 },
    ...over,
  }
}
