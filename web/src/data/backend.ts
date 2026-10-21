import type { Role } from '../alerts/types'
import type { LiveState, RequestRecord, SendInput, SessionUser, UserRecord, WithId } from './types'

export type Unsubscribe = () => void

/** Entrada e saída da conta (Google). */
export interface AuthPort {
  onUserChanged(cb: (u: SessionUser | null) => void): Unsubscribe
  signIn(): Promise<void>
  signOut(): Promise<void>
}

/** Tudo o que as telas precisam do banco. Os adaptadores do Firebase e os fakes dos testes a implementam. */
export interface Backend {
  ensureUser(u: SessionUser): Promise<void>
  watchUser(uid: string, cb: (r: UserRecord | null) => void): Unsubscribe
  watchLive(cb: (l: LiveState | null) => void): Unsubscribe
  watchMyRequests(uid: string, cb: (list: WithId<RequestRecord>[]) => void): Unsubscribe
  /** `id` é o uid. */
  watchUsers(cb: (list: WithId<UserRecord>[]) => void): Unsubscribe
  watchConnected(cb: (online: boolean) => void): Unsubscribe
  /** Hora do servidor em ms (`Date.now()` + `.info/serverTimeOffset`). */
  serverNow(): number
  /** Devolve o id do pedido; sem conexão tem de falhar na hora, sem guardar nada em silêncio. */
  send(input: SendInput): Promise<string>
  cancel(id: string): Promise<void>
  setRole(uid: string, role: Role): Promise<void>
  setBlocked(uid: string, blocked: boolean): Promise<void>
}
