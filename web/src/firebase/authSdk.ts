import type { FirebaseApp } from 'firebase/app'
import {
  type Auth,
  connectAuthEmulator,
  getAuth,
  getRedirectResult,
  GoogleAuthProvider,
  onAuthStateChanged,
  signInWithCredential,
  signInWithPopup,
  signInWithRedirect,
  signOut,
} from 'firebase/auth'
import type { AuthPort, Unsubscribe } from '../data/backend'
import type { SessionUser } from '../data/types'
import { AUTH_EMULATOR_URL } from './init'

// O adaptador do Auth: este arquivo importa o SDK do Auth (pesado) e só é carregado por `auth.ts`, com
// `import()`, fora do pacote inicial.

/** Erros em que o popup não funciona neste navegador: vale o redirecionamento. */
const POPUP_UNAVAILABLE = [
  'auth/popup-blocked',
  'auth/operation-not-supported-in-this-environment',
  'auth/web-storage-unsupported',
]

function codeOf(e: unknown): string {
  return typeof e === 'object' && e !== null && typeof (e as { code?: unknown }).code === 'string'
    ? (e as { code: string }).code
    : ''
}

/** O app instalado no iPhone (tela inicial) não segura o popup do Google: vai direto pelo redirecionamento. */
function standaloneIos(): boolean {
  const ua = typeof navigator === 'undefined' ? '' : navigator.userAgent
  const ios = /iPad|iPhone|iPod/.test(ua)
  const standalone =
    typeof navigator !== 'undefined' && (navigator as Navigator & { standalone?: boolean }).standalone === true
  return ios && standalone
}

export interface E2EHook {
  signIn(email: string, name: string): Promise<void>
}

declare global {
  interface Window {
    __alertaE2E?: E2EHook
  }
}

function toSessionUser(u: { uid: string; displayName: string | null; email: string | null }): SessionUser {
  return { uid: u.uid, name: u.displayName ?? '', email: u.email ?? '' }
}

export function createFirebaseAuthPort(app: FirebaseApp, opts: { useEmulators: boolean }): AuthPort {
  const auth: Auth = getAuth(app)
  auth.languageCode = 'pt-BR'
  if (opts.useEmulators) {
    connectAuthEmulator(auth, AUTH_EMULATOR_URL, { disableWarnings: true })
    // Só no emulador: entrar sem a tela do Google (teste de ponta a ponta). Nunca existe na nuvem.
    window.__alertaE2E = {
      async signIn(email: string, name: string) {
        const credential = GoogleAuthProvider.credential(
          JSON.stringify({ sub: email, email, email_verified: true, name }),
        )
        await signInWithCredential(auth, credential)
      },
    }
  }
  // Volta do redirecionamento: o `onAuthStateChanged` abaixo já entrega o usuário; aqui só se completa o fluxo.
  void getRedirectResult(auth).catch(() => {})

  const provider = new GoogleAuthProvider()
  provider.setCustomParameters({ prompt: 'select_account' })

  return {
    onUserChanged(cb: (u: SessionUser | null) => void): Unsubscribe {
      return onAuthStateChanged(auth, (u) => cb(u ? toSessionUser(u) : null))
    },
    async signIn() {
      if (standaloneIos()) {
        await signInWithRedirect(auth, provider)
        return
      }
      try {
        await signInWithPopup(auth, provider)
      } catch (e) {
        if (POPUP_UNAVAILABLE.includes(codeOf(e))) {
          await signInWithRedirect(auth, provider)
          return
        }
        throw e
      }
    },
    async signOut() {
      await signOut(auth)
    },
  }
}
