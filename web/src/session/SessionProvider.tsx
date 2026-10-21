import { createContext, type ReactNode, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { Role } from '../alerts/types'
import type { AuthPort, Backend } from '../data/backend'
import type { SessionUser, UserRecord } from '../data/types'

export type SessionState =
  | { status: 'loading' }
  | { status: 'signedOut' }
  | { status: 'blocked'; user: SessionUser }
  | { status: 'active'; user: SessionUser; record: UserRecord; role: Role }

export type ActiveSession = Extract<SessionState, { status: 'active' }>

type Session = SessionState & { signIn(): Promise<void>; signOut(): Promise<void> }

const SessionContext = createContext<Session | null>(null)

interface Raw {
  user: SessionUser | null | undefined
  record: UserRecord | null | undefined
}

function derive(raw: Raw): SessionState {
  if (raw.user === undefined) return { status: 'loading' }
  if (raw.user === null) return { status: 'signedOut' }
  // Entrou, mas o registro de /users ainda não chegou.
  if (raw.record == null) return { status: 'loading' }
  if (raw.record.blocked) return { status: 'blocked', user: raw.user }
  return { status: 'active', user: raw.user, record: raw.record, role: raw.record.role }
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
    return backend.watchUser(uid, (record) => setRaw((prev) => ({ ...prev, record })))
  }, [backend, uid])

  const signIn = useCallback(() => auth.signIn(), [auth])
  const signOut = useCallback(() => auth.signOut(), [auth])
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
