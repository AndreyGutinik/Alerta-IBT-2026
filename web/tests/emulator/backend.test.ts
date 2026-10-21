import { deleteApp, initializeApp } from 'firebase/app'
import { type Auth, connectAuthEmulator, getAuth, GoogleAuthProvider, signInWithCredential } from 'firebase/auth'
import { connectDatabaseEmulator, type Database, getDatabase, goOffline, goOnline } from 'firebase/database'
import { afterAll, beforeEach, describe, expect, it } from 'vitest'
import type { LiveState, RequestRecord, SessionUser, UserRecord, WithId } from '../../src/data/types'
import { createFirebaseAuthPort } from '../../src/firebase/authSdk'
import { FirebaseBackend } from '../../src/firebase/backend'
import { OfflineError } from '../../src/firebase/sender'
import { clearAuthAccounts, restDelete, restGet, restPatch, restPut } from '../support/rest'
import { PROJECT_ID } from '../support/rulesEnv'

// Auth + Realtime Database de verdade, nos emuladores (`npm run test:emu`), com as regras do projeto.

const OPTIONS = {
  apiKey: 'demo-key',
  authDomain: `${PROJECT_ID}.firebaseapp.com`,
  projectId: PROJECT_ID,
  databaseURL: `https://${PROJECT_ID}-default-rtdb.firebaseio.com`,
  appId: '1:000000000000:web:0000000000000000',
}

interface Client {
  auth: Auth
  db: Database
  backend: FirebaseBackend
  user: SessionUser
  close(): Promise<void>
}

/** Espera `check` ser verdadeiro em algum valor de uma assinatura (e solta a assinatura). */
function until<T>(subscribe: (cb: (v: T) => void) => () => void, check: (v: T) => boolean, ms = 5000): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let stop: () => void = () => {}
    let done = false
    const timer = setTimeout(() => {
      done = true
      stop()
      reject(new Error('tempo esgotado esperando o valor'))
    }, ms)
    stop = subscribe((v) => {
      if (done || !check(v)) return
      done = true
      clearTimeout(timer)
      queueMicrotask(() => stop())
      resolve(v)
    })
  })
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

let counter = 0
const clients: Client[] = []

/** Um app do Firebase só para esta pessoa (como um celular), já conectado e logado com Google falso. */
async function client(email: string, name: string): Promise<Client> {
  counter += 1
  const app = initializeApp(OPTIONS, `t${counter}`)
  const auth = getAuth(app)
  connectAuthEmulator(auth, 'http://127.0.0.1:9099', { disableWarnings: true })
  const db = getDatabase(app)
  connectDatabaseEmulator(db, '127.0.0.1', 9000)
  const backend = new FirebaseBackend(db)
  await until<boolean>((cb) => backend.watchConnected(cb), (on) => on === true)
  const credential = GoogleAuthProvider.credential(JSON.stringify({ sub: email, email, email_verified: true, name }))
  const { user } = await signInWithCredential(auth, credential)
  const c: Client = {
    auth,
    db,
    backend,
    user: { uid: user.uid, name, email },
    async close() {
      backend.dispose()
      goOffline(db)
      await deleteApp(app)
    },
  }
  clients.push(c)
  return c
}

const PREVIEW = { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' } as const
const sendInput = (c: Client, raw = 'gol branco ffo4745', urgent = false) => ({
  uid: c.user.uid,
  name: c.user.name,
  raw,
  urgent,
  preview: PREVIEW,
})

beforeEach(async () => {
  await restDelete('')
  await clearAuthAccounts()
})

afterAll(async () => {
  await Promise.all(clients.splice(0).map((c) => c.close()))
})

describe('FirebaseBackend + Auth nos emuladores', () => {
  it('ensureUser cria o registro como member e, na volta, só atualiza nome e último uso', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)

    const created = (await restGet(`users/${alice.user.uid}`)) as UserRecord
    expect(created).toMatchObject({ name: 'Alice', email: 'alice@example.com', role: 'member', blocked: false })
    expect(typeof created.createdAt).toBe('number')

    await restPatch(`users/${alice.user.uid}`, { role: 'admin' }) // promovida pela CLI
    await alice.backend.ensureUser({ ...alice.user, name: 'Alice Souza' })
    const again = (await restGet(`users/${alice.user.uid}`)) as UserRecord
    expect(again.role).toBe('admin') // ensureUser não mexe no cargo
    expect(again.name).toBe('Alice Souza')
    expect(again.createdAt).toBe(created.createdAt)
    expect(again.lastSeenAt!).toBeGreaterThanOrEqual(created.lastSeenAt!)
  })

  it('ensureUser não toca em quem está bloqueado', async () => {
    const carol = await client('carol@example.com', 'Carol')
    await carol.backend.ensureUser(carol.user)
    await restPatch(`users/${carol.user.uid}`, { blocked: true })
    const before = (await restGet(`users/${carol.user.uid}`)) as UserRecord
    await carol.backend.ensureUser({ ...carol.user, name: 'Outro Nome' })
    expect(await restGet(`users/${carol.user.uid}`)).toEqual(before)
  })

  it('watchUser acompanha o registro ao vivo (bloqueio no meio do uso)', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)
    const seen: (UserRecord | null)[] = []
    const stop = alice.backend.watchUser(alice.user.uid, (r) => seen.push(r))
    await until<UserRecord | null>((cb) => alice.backend.watchUser(alice.user.uid, cb), (r) => r?.role === 'member')
    await restPatch(`users/${alice.user.uid}`, { blocked: true })
    await until<UserRecord | null>((cb) => alice.backend.watchUser(alice.user.uid, cb), (r) => r?.blocked === true)
    stop()
    expect(seen[0]?.blocked).toBe(false)
  })

  it('send cria o pedido pelo id do aparelho; watchMyRequests devolve o raw; o segundo envio em seguida é recusado', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)

    const id = await alice.backend.send(sendInput(alice))
    const stored = (await restGet(`requests/${id}`)) as RequestRecord
    expect(stored).toMatchObject({ uid: alice.user.uid, raw: 'gol branco ffo4745', cancel: false, urgent: false })
    expect(stored.status).toBeUndefined()
    const userNow = (await restGet(`users/${alice.user.uid}`)) as UserRecord
    expect(userNow.lastRequestId).toBe(id)
    expect(userNow.sentCount).toBe(1)

    const mine = await until<WithId<RequestRecord>[]>(
      (cb) => alice.backend.watchMyRequests(alice.user.uid, cb),
      (l) => l.length === 1,
    )
    expect(mine[0].raw).toBe('gol branco ffo4745')
    expect(mine[0].id).toBe(id)

    await expect(alice.backend.send(sendInput(alice, 'onix preto abc1d23'))).rejects.toMatchObject({
      message: expect.stringMatching(/permission_denied/i),
    })
    expect(Object.keys((await restGet('requests')) as object)).toEqual([id])
  })

  it('cancel grava cancel = true (e watchMyRequests mostra)', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)
    const id = await alice.backend.send(sendInput(alice))
    await alice.backend.cancel(id)
    expect(await restGet(`requests/${id}/cancel`)).toBe(true)
    const mine = await until<WithId<RequestRecord>[]>(
      (cb) => alice.backend.watchMyRequests(alice.user.uid, cb),
      (l) => l[0]?.cancel === true,
    )
    expect(mine[0].cancel).toBe(true)
  })

  it('o status que o PC escreve chega em watchMyRequests', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)
    const id = await alice.backend.send(sendInput(alice))
    await restPut(`requests/${id}/status`, { state: 'queued', position: 1, at: Date.now() })
    const mine = await until<WithId<RequestRecord>[]>(
      (cb) => alice.backend.watchMyRequests(alice.user.uid, cb),
      (l) => l[0]?.status?.state === 'queued',
    )
    expect(mine[0].status).toMatchObject({ state: 'queued', position: 1 })
  })

  it('admin promove, rebaixa, bloqueia e restaura; membro não lê a lista; ninguém muda o próprio cargo', async () => {
    const alice = await client('alice@example.com', 'Alice')
    const bob = await client('bob@example.com', 'Bob')
    await alice.backend.ensureUser(alice.user)
    await bob.backend.ensureUser(bob.user)

    // Membro: sem acesso à lista de usuários (a assinatura falha e entrega lista vazia)
    const asMember = await until<WithId<UserRecord>[]>((cb) => alice.backend.watchUsers(cb), () => true)
    expect(asMember).toEqual([])

    await restPatch(`users/${bob.user.uid}`, { role: 'admin' }) // a CLI da instalação promove o primeiro admin
    const list = await until<WithId<UserRecord>[]>((cb) => bob.backend.watchUsers(cb), (l) => l.length === 2)
    expect(list.map((u) => u.email).sort()).toEqual(['alice@example.com', 'bob@example.com'])

    await bob.backend.setRole(alice.user.uid, 'admin')
    expect(await restGet(`users/${alice.user.uid}/role`)).toBe('admin')
    await bob.backend.setRole(alice.user.uid, 'member')
    expect(await restGet(`users/${alice.user.uid}/role`)).toBe('member')

    await bob.backend.setBlocked(alice.user.uid, true)
    await until<UserRecord | null>((cb) => alice.backend.watchUser(alice.user.uid, cb), (r) => r?.blocked === true)
    await expect(alice.backend.send(sendInput(alice))).rejects.toBeDefined()
    await bob.backend.setBlocked(alice.user.uid, false)
    await until<UserRecord | null>((cb) => alice.backend.watchUser(alice.user.uid, cb), (r) => r?.blocked === false)
    await expect(alice.backend.send(sendInput(alice))).resolves.toEqual(expect.any(String))

    await expect(bob.backend.setRole(bob.user.uid, 'member')).rejects.toBeDefined()
    await expect(bob.backend.setBlocked(bob.user.uid, true)).rejects.toBeDefined()
  })

  it('restPut("live") com queue como objeto: watchLive entrega queue como array', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)
    await restPut('live', {
      heartbeatAt: { '.sv': 'timestamp' },
      led: true,
      paused: false,
      now: null,
      queue: { 0: { id: 'a', text: 'GOL', source: 'site' }, 2: { id: 'b', text: 'UNO', source: 'pc', urgent: true } },
      bar: { position: 'top', height: 56, speed: 90 },
    })
    const live = await until<LiveState | null>((cb) => alice.backend.watchLive(cb), (l) => l !== null)
    expect(Array.isArray(live?.queue)).toBe(true)
    expect(live?.queue.map((q) => q.id)).toEqual(['a', 'b'])
    expect(live?.led).toBe(true)
    expect(live?.recent).toEqual([])
  })

  it('serverNow fica a menos de 5 s de Date.now()', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await sleep(300)
    expect(Math.abs(alice.backend.serverNow() - Date.now())).toBeLessThan(5000)
  })

  it('sem conexão: send, cancel e as ações da equipe falham na hora e nada é gravado quando a rede volta', async () => {
    const alice = await client('alice@example.com', 'Alice')
    await alice.backend.ensureUser(alice.user)
    const bob = await client('bob@example.com', 'Bob')
    await bob.backend.ensureUser(bob.user)
    await restPatch(`users/${bob.user.uid}`, { role: 'admin' })

    // Desliga só o banco dela (como o modo avião) e espera o SDK perceber.
    goOffline(alice.db)
    await until<boolean>((cb) => alice.backend.watchConnected(cb), (on) => on === false)

    const started = Date.now()
    await expect(alice.backend.send(sendInput(alice))).rejects.toBeInstanceOf(OfflineError)
    await expect(alice.backend.cancel('qualquer')).rejects.toBeInstanceOf(OfflineError)
    await expect(alice.backend.setRole(bob.user.uid, 'member')).rejects.toBeInstanceOf(OfflineError)
    await expect(alice.backend.setBlocked(bob.user.uid, true)).rejects.toBeInstanceOf(OfflineError)
    expect(Date.now() - started).toBeLessThan(500)

    goOnline(alice.db)
    await until<boolean>((cb) => alice.backend.watchConnected(cb), (on) => on === true)
    await sleep(500) // se algo tivesse ficado na fila do SDK, chegaria agora
    expect(await restGet('requests')).toBeNull()
    expect(((await restGet(`users/${alice.user.uid}`)) as UserRecord).sentCount).toBeUndefined()
    const bobNow = (await restGet(`users/${bob.user.uid}`)) as UserRecord
    expect(bobNow.role).toBe('admin')
    expect(bobNow.blocked).toBe(false)
  })

  it('AuthPort real: entrega o usuário do Google falso e sai', async () => {
    const app = initializeApp(OPTIONS, 'auth-port')
    const auth = getAuth(app)
    connectAuthEmulator(auth, 'http://127.0.0.1:9099', { disableWarnings: true })
    const port = createFirebaseAuthPort(app, { useEmulators: false })
    await until<SessionUser | null>((cb) => port.onUserChanged(cb), (u) => u === null)
    const credential = GoogleAuthProvider.credential(
      JSON.stringify({ sub: 'zoe@example.com', email: 'zoe@example.com', email_verified: true, name: 'Zoe' }),
    )
    await signInWithCredential(auth, credential)
    const user = await until<SessionUser | null>((cb) => port.onUserChanged(cb), (u) => u !== null)
    expect(user).toMatchObject({ name: 'Zoe', email: 'zoe@example.com' })
    expect(user?.uid).toBeTruthy()
    await port.signOut()
    await until<SessionUser | null>((cb) => port.onUserChanged(cb), (u) => u === null)
    await deleteApp(app)
  })
})
