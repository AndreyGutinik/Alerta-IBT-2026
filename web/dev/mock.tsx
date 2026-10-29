// Modo "mock" só para olhar as telas (npm run dev, abrir /dev/mock.html): nenhum Firebase, nenhuma conta real.
// Parâmetros: ?state=active|signedOut|blocked|loading  &role=admin|member  &pc=on|off|paused|ledoff|idle
//             &offline=1 (sem conexão)  &empty=1 (sem avisos, fila e recentes)  &tutorial=1 (mostra o tutorial)
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import type { Role } from '../src/alerts/types'
import type { AuthPort, Backend, Unsubscribe } from '../src/data/backend'
import type { LiveState, RequestRecord, SessionUser, UserRecord, WithId } from '../src/data/types'
import '../src/styles/fonts.css'
import '../src/styles/tokens.css'
import '../src/styles/base.css'
import '../src/styles/app.css'
import { Root } from '../src/ui/Root'
import { TUTORIAL_KEY } from '../src/ui/Tutorial'

const q = new URLSearchParams(location.search)
const role: Role = q.get('role') === 'admin' ? 'admin' : 'member'
const state = q.get('state') ?? 'active'
const pc = q.get('pc') ?? 'on'
const empty = q.get('empty') === '1'
const offline = q.get('offline') === '1'

try {
  if (q.get('tutorial') === '1') localStorage.removeItem(TUTORIAL_KEY)
  else localStorage.setItem(TUTORIAL_KEY, '1')
} catch {
  // sem armazenamento
}

const T0 = Date.now()
const MIN = 60_000
const user: SessionUser = { uid: 'mock-u1', name: 'Joana Silva', email: 'joana@example.com' }

function userRecord(): UserRecord {
  return { name: user.name, email: user.email, role, blocked: state === 'blocked', createdAt: T0 - 90 * 86_400_000 }
}

function live(): LiveState {
  const base: LiveState = {
    heartbeatAt: pc === 'off' ? T0 - 10 * MIN : T0,
    led: pc !== 'ledoff',
    paused: pc === 'paused',
    bar: { position: 'top', height: 56, speed: 90 },
    now:
      pc === 'idle' || empty
        ? null
        : {
            id: 'r-now',
            text: 'COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO',
            source: 'site',
            startedAt: T0 - 9_000,
            durationMs: 26_000,
          },
    queue: empty
      ? []
      : [
          { id: 'r-q1', text: 'GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO', source: 'site', urgent: true },
          { id: 'r-q2', text: 'HB20 PRETO | PLACA: RIO-2A18 | PORTA ABERTA', source: 'telegram' },
          { id: 'r-q3', text: 'PAIS DO PEDRO CHEGOU', source: 'pc' },
        ],
    recent: empty
      ? []
      : [
          { id: 'r-h1', text: 'ONIX CINZA | PLACA: BRA-2E19 | ALARME TOCANDO', state: 'shown', at: T0 - 4 * MIN },
          { id: 'r-h2', text: 'TEXTO NÃO ACEITO', state: 'rejected', at: T0 - 9 * MIN },
          { id: 'r-h3', text: 'FIAT UNO VERMELHO | PLACA: QWE-1R34', state: 'cancelled', at: T0 - 15 * MIN },
          { id: 'r-h4', text: 'KA AZUL | PLACA: JKL-9B87 | VIDRO ABERTO', state: 'expired', at: T0 - 30 * MIN },
        ],
  }
  return base
}

function requests(): WithId<RequestRecord>[] {
  if (empty) return []
  const mk = (id: string, text: string, ago: number, status?: RequestRecord['status']): WithId<RequestRecord> => ({
    id,
    uid: user.uid,
    name: user.name,
    raw: text.toLowerCase(),
    urgent: false,
    createdAt: T0 - ago,
    cancel: false,
    preview: { text, kind: 'vehicle', by: 'local' },
    ...(status ? { status } : {}),
  })
  return [
    mk('r-now', 'COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO', 20_000, { state: 'showing', at: T0 - 9_000 }),
    mk('r-q1', 'GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO', 15_000, { state: 'queued', position: 1, at: T0 - 14_000 }),
    mk('r-2', 'ONIX CINZA | PLACA: BRA-2E19 | ALARME TOCANDO', 4 * MIN, { state: 'shown', at: T0 - 3 * MIN }),
    mk('r-3', 'SEM PLACA', 9 * MIN, { state: 'rejected', reason: 'faltou placa', at: T0 - 9 * MIN }),
    mk('r-4', 'FIAT UNO VERMELHO | PLACA: QWE-1R34', 15 * MIN, { state: 'cancelled', at: T0 - 14 * MIN }),
  ]
}

function members(): WithId<UserRecord>[] {
  const mk = (id: string, name: string, email: string, over: Partial<UserRecord>): WithId<UserRecord> => ({
    id,
    name,
    email,
    role: 'member',
    blocked: false,
    createdAt: T0 - 60 * 86_400_000,
    lastSeenAt: T0 - 3 * 3_600_000,
    sentCount: 12,
    ...over,
  })
  return [
    mk('o1', 'Andrey Gutnik', 'gutnikandrey01@protonmail.com', { role: 'admin', sentCount: 87 }),
    mk(user.uid, user.name, user.email, { role: 'admin', sentCount: 31 }),
    mk('m3', 'Carlos Pereira', 'carlos@example.com', { sentCount: 9 }),
    mk('m4', 'Beatriz Lima', 'bia@example.com', { createdAt: T0 - 86_400_000, lastSeenAt: undefined, sentCount: 0 }),
    mk('m5', 'Rafael Souza', 'rafa@example.com', { blocked: true, sentCount: 4 }),
  ]
}

function channel<T>(value: T) {
  return (cb: (v: T) => void): Unsubscribe => {
    cb(value)
    return () => {}
  }
}

const backend: Backend = {
  ensureUser: () => Promise.resolve(),
  watchUser: (_uid, cb) => channel<UserRecord | null>(userRecord())(cb),
  watchLive: channel<LiveState | null>(live()),
  watchMyRequests: (_uid, cb) => channel(requests())(cb),
  watchUsers: channel(members()),
  watchConnected: channel(!offline),
  serverNow: () => Date.now(),
  send: () => Promise.resolve('mock'),
  cancel: () => Promise.resolve(),
  setRole: () => Promise.resolve(),
  setBlocked: () => Promise.resolve(),
}

const auth: AuthPort = {
  onUserChanged: (cb) => {
    if (state === 'loading') return () => {}
    cb(state === 'signedOut' ? null : user)
    return () => {}
  },
  signIn: () => Promise.resolve(),
  signOut: () => Promise.resolve(),
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root auth={auth} backend={backend} corrector={Promise.resolve(null)} />
  </StrictMode>,
)
