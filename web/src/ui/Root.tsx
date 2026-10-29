import { useEffect, useState } from 'react'
import logo from '../../../assets/icons/logo.svg'
import type { AiCorrector } from '../correction/types'
import type { AuthPort, Backend } from '../data/backend'
import { SessionProvider, useSession } from '../session/SessionProvider'
import { AppDataProvider } from '../state/AppData'
import { Blocked } from './Blocked'
import { Login } from './Login'
import { Shell } from './Shell'
import { readTutorialDone, Tutorial } from './Tutorial'

export interface RootProps {
  auth: AuthPort
  backend: Backend
  /** A correção por IA: chega quando o Firebase AI Logic carregar; `null` (ou erro) = só o parser local. */
  corrector: Promise<AiCorrector | null>
}

const SLOW_LOADING_MS = 10_000

function Loading() {
  const { signOut } = useSession()
  const [slow, setSlow] = useState(false)
  useEffect(() => {
    const id = setTimeout(() => setSlow(true), SLOW_LOADING_MS)
    return () => clearTimeout(id)
  }, [])
  return (
    <main className="splash" aria-busy="true">
      <div className="cd splash-card">
        <img className="logo" src={logo} alt="IBT Controller" width={72} height={72} />
        <p className="sub slow" role="status">
          {slow ? 'Está demorando. Confira a internet.' : ''}
        </p>
        {slow && (
          <button type="button" className="gbtn" onClick={() => void signOut()}>
            Sair
          </button>
        )}
      </div>
    </main>
  )
}

function Gate({ backend, corrector }: { backend: Backend; corrector: AiCorrector | null }) {
  const session = useSession()
  const [tutorialDone, setTutorialDone] = useState(readTutorialDone)

  switch (session.status) {
    case 'loading':
      return <Loading />
    case 'signedOut':
      return <Login />
    case 'blocked':
      return <Blocked />
    case 'active':
      return (
        <AppDataProvider backend={backend} corrector={corrector}>
          {tutorialDone ? <Shell /> : <Tutorial onDone={() => setTutorialDone(true)} />}
        </AppDataProvider>
      )
  }
}

export function Root({ auth, backend, corrector }: RootProps) {
  const [resolved, setResolved] = useState<AiCorrector | null>(null)
  useEffect(() => {
    let alive = true
    corrector.then(
      (c) => {
        if (alive) setResolved(c)
      },
      () => {},
    )
    return () => {
      alive = false
    }
  }, [corrector])

  return (
    <SessionProvider auth={auth} backend={backend}>
      <Gate backend={backend} corrector={resolved} />
    </SessionProvider>
  )
}
