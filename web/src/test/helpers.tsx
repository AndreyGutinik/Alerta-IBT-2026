import { render } from '@testing-library/react'
import type { ReactElement, ReactNode } from 'react'
import type { Role } from '../alerts/types'
import type { AiCorrector } from '../correction/types'
import type { LiveState, UserRecord } from '../data/types'
import { SessionProvider, useSession } from '../session/SessionProvider'
import { AppDataProvider } from '../state/AppData'
import { FakeAuth, FakeBackend, liveState, TEST_USER, userRecord } from './fakes'

function ActiveOnly({
  backend,
  corrector,
  children,
}: {
  backend: FakeBackend
  corrector: AiCorrector | null
  children: ReactNode
}) {
  const session = useSession()
  if (session.status !== 'active') return null
  return (
    <AppDataProvider backend={backend} corrector={corrector}>
      {children}
    </AppDataProvider>
  )
}

export interface RenderOptions {
  role?: Role
  record?: Partial<UserRecord>
  /** `/live` inicial; omitido = um saudável; `null` = nenhum. */
  live?: LiveState | null
  corrector?: AiCorrector | null
  backend?: FakeBackend
}

/** Monta uma tela dentro de sessão ativa e dados (sem passar por Root/Shell), com os falsos à vista. */
export function renderActive(ui: ReactElement, opts: RenderOptions = {}) {
  const backend = opts.backend ?? new FakeBackend()
  const auth = new FakeAuth(TEST_USER)
  backend.emitUser(userRecord({ role: opts.role ?? 'member', ...opts.record }))
  backend.emitLive(opts.live === undefined ? liveState({}, backend.now) : opts.live)
  const view = render(
    <SessionProvider auth={auth} backend={backend}>
      <ActiveOnly backend={backend} corrector={opts.corrector ?? null}>
        {ui}
      </ActiveOnly>
    </SessionProvider>,
  )
  return { ...view, backend, auth }
}
