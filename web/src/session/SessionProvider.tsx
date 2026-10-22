import { createContext, type ReactNode, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { Role } from '../alerts/types'
import type { AuthPort, Backend } from '../data/backend'
import type { SessionUser, UserRecord } from '../data/types'
import { isOwnerEmail } from './owner'

export type SessionState =
  | { status: 'loading' }
  | { status: 'signedOut' }
  | { status: 'blocked'; user: SessionUser }
  | { status: 'active'; user: SessionUser; record: UserRecord; role: Role }

export type ActiveSession = Extract<SessionState, { status: 'active' }>

type Session = SessionState & { signIn(): Promise<void>; signOut(): Promise<void> }

const SessionContext = createContext<Session | null>(null)

const RECORD_KEY_PREFIX = 'alertaibt.record.v1.'

/** O último registro da pessoa visto neste aparelho (só o que a tela usa), para abrir sem rede. */
export function readCachedRecord(uid: string): UserRecord | null {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(RECORD_KEY_PREFIX + uid) ?? 'null')
    if (typeof parsed !== 'object' || parsed === null) return null
    const p = parsed as Record<string, unknown>
    if (typeof p.name !== 'string' || typeof p.email !== 'string') return null
    if (p.role !== 'admin' && p.role !== 'member') return null
    if (typeof p.blocked !== 'boolean') return null
    return { name: p.name, email: p.email, role: p.role, blocked: p.blocked, createdAt: 0 }
  } catch {
    return null
  }
}

function cacheRecord(uid: string, r: UserRecord): void {
  try {
    localStorage.setItem(
      RECORD_KEY_PREFIX + uid,
      JSON.stringify({ name: r.name, email: r.email, role: r.role, blocked: r.blocked }),
    )
  } catch {
    // sem armazenamento: só não abre sem rede
  }
}

function clearCachedRecord(uid: string): void {
  try {
    localStorage.removeItem(RECORD_KEY_PREFIX + uid)
  } catch {
    // nada a fazer
  }
}

interface Raw {
  user: SessionUser | null | undefined
  record: UserRecord | null | undefined
}

function derive(raw: Raw): SessionState {
  if (raw.user === undefined) return { status: 'loading' }
  if (raw.user === null) return { status: 'signedOut' }
  // Entrou, mas o registro de /users ainda não chegou: sem rede, vale o último que este aparelho viu (as
  // escritas exigem conexão e as regras do banco decidem de verdade; chegando o registro, ele vale).
  const record = raw.record === undefined ? readCachedRecord(raw.user.uid) : raw.record
  if (record == null) return { status: 'loading' }
  // O mantenedor nunca fica bloqueado: as regras já impedem, e aqui a tela de bloqueio nunca aparece para ele.
  if (isOwnerEmail(raw.user.email) || isOwnerEmail(record.email)) {
    return { status: 'active', user: raw.user, record: record.blocked ? { ...record, blocked: false } : record, role: record.role }
  }
  if (record.blocked) return { status: 'blocked', user: raw.user }
  return { status: 'active', user: raw.user, record, role: record.role }
}

/**
 * A sessão: Auth (Google) + o registro da pessoa em /users. Quem entra tem o registro criado (`ensureUser`,
 * erro ignorado) e acompanhado ao vivo, então bloqueio e promoção valem no meio do uso.
 */
export function SessionProvider({
  auth,
  backend,
  children,
}: {
  auth: AuthPort
  backend: Backend
  children: ReactNode
}) {
  const [raw, setRaw] = useState<Raw>({ user: undefined, record: undefined })

  useEffect(
    () =>
      auth.onUserChanged((user) => {
        // A mesma pessoa de novo (ex.: renovação do token) não derruba o registro já carregado.
        setRaw((prev) => ({ user, record: prev.user?.uid === user?.uid ? prev.record : undefined }))
        if (user) backend.ensureUser(user).catch(() => {})
      }),
    [auth, backend],
  )

  // O uid, não o objeto: o Auth pode devolver um objeto novo para a mesma pessoa.
  const uid = raw.user?.uid ?? null

  useEffect(() => {
    if (!uid) return undefined
    return backend.watchUser(uid, (record) => {
      if (record) cacheRecord(uid, record)
      setRaw((prev) => ({ ...prev, record }))
    })
  }, [backend, uid])

  const signIn = useCallback(() => auth.signIn(), [auth])
  const signOut = useCallback(async () => {
    if (uid) clearCachedRecord(uid)
    await auth.signOut()
  }, [auth, uid])
  const state = useMemo(() => derive(raw), [raw])
  const value = useMemo<Session>(() => ({ ...state, signIn, signOut }), [state, signIn, signOut])

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

export function useSession(): Session {
  const session = useContext(SessionContext)
  if (!session) throw new Error('useSession fora do SessionProvider')
  return session
}

/** Só para telas montadas com sessão ativa. */
export function useActiveSession(): ActiveSession & { signOut(): Promise<void> } {
  const session = useSession()
  if (session.status !== 'active') throw new Error('Sessão não está ativa')
  return session
}
