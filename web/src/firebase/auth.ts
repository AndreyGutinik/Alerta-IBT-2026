import type { FirebaseApp } from 'firebase/app'
import type { AuthPort, Unsubscribe } from '../data/backend'
import type { SessionUser } from '../data/types'

/**
 * O `AuthPort` do Firebase com o SDK do Auth carregado sob demanda (pedaço de JS separado do pacote
 * inicial). Se o pedaço não carregar (sem rede e fora do cache), a pessoa cai na tela de entrada e o
 * erro aparece ao tentar entrar.
 */
export function createLazyAuth(app: FirebaseApp, opts: { useEmulators: boolean }): AuthPort {
  let loaded: Promise<AuthPort> | null = null
  const load = (): Promise<AuthPort> => {
    loaded ??= import('./authSdk').then((m) => m.createFirebaseAuthPort(app, opts))
    return loaded
  }

  return {
    onUserChanged(cb: (u: SessionUser | null) => void): Unsubscribe {
      let stop: Unsubscribe | undefined
      let cancelled = false
      load().then(
        (port) => {
          if (!cancelled) stop = port.onUserChanged(cb)
        },
        () => {
          loaded = null
          if (!cancelled) cb(null)
        },
      )
      return () => {
        cancelled = true
        stop?.()
      }
    },
    signIn: () => load().then((port) => port.signIn()),
    signOut: () => load().then((port) => port.signOut()),
  }
}
