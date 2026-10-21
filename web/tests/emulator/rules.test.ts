import { assertFails, assertSucceeds } from '@firebase/rules-unit-testing'
import { serverTimestamp } from 'firebase/database'
import { describe, expect, it } from 'vitest'
import type { SendInput } from '../../src/data/types'
import { buildSendUpdate, buildUserRecord } from '../../src/data/writes'
import { asAnonymous, asPc, asUser, seed, setupRulesEnv } from '../support/rulesEnv'

setupRulesEnv()

const PREVIEW = { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' } as const

function input(uid: string, extra: Partial<SendInput> = {}): SendInput {
  return { uid, name: uid, raw: 'gol branco ffo4745', urgent: false, preview: PREVIEW, ...extra }
}

type Update = Record<string, unknown>

/** O envio do app (`buildSendUpdate`), com um ajuste opcional no pedido para os casos de erro. */
function sendUpdate(id: string, uid: string, tweak?: (request: Record<string, unknown>) => void, extra?: Partial<SendInput>): Update {
  const update = buildSendUpdate(id, input(uid, extra))
  if (tweak) tweak(update[`requests/${id}`] as Record<string, unknown>)
  return update
}

const send = (db: ReturnType<typeof asUser>, update: Update) => db.ref().update(update)

/** Pedido já gravado (semente), como o PC o deixaria. */
async function seedRequest(id: string, uid: string, status?: Record<string, unknown>, cancel = false) {
  await seed(`requests/${id}`, {
    uid,
    name: uid,
    raw: 'gol branco ffo4745',
    urgent: false,
    createdAt: Date.now() - 1000,
    cancel,
    ...(status ? { status } : {}),
  })
}

describe('users', () => {
  it('quem entra pela primeira vez cria o próprio registro como member', async () => {
    const record = buildUserRecord({ uid: 'dave', name: 'Dave', email: 'dave@example.com' })

    await assertSucceeds(asUser('dave').ref('users/dave').set(record))
  })

  it.each([
    ['com role admin', (r: Update) => ({ ...r, role: 'admin' })],
    ['com blocked true', (r: Update) => ({ ...r, blocked: true })],
    ['com campo extra', (r: Update) => ({ ...r, vip: true })],
    ['com e-mail diferente do token', (r: Update) => ({ ...r, email: 'outro@example.com' })],
    ['com nome vazio', (r: Update) => ({ ...r, name: '' })],
    ['com createdAt do cliente', (r: Update) => ({ ...r, createdAt: 1 })],
    ['já com lastSentAt', (r: Update) => ({ ...r, lastSentAt: serverTimestamp() })],
    ['já com sentCount', (r: Update) => ({ ...r, sentCount: 1 })],
  ])('criar o próprio registro %s é negado', async (_name, tweak) => {
    const record = tweak(buildUserRecord({ uid: 'dave', name: 'Dave', email: 'dave@example.com' }))

    await assertFails(asUser('dave').ref('users/dave').set(record))
  })

  it('criar o registro de outro uid é negado', async () => {
    const record = buildUserRecord({ uid: 'dave', name: 'Dave', email: 'dave@example.com' })

    await assertFails(asUser('dave').ref('users/erik').set(record))
  })

  it('só login Google cria registro (senha e anônimo não)', async () => {
    const record = buildUserRecord({ uid: 'dave', name: 'Dave', email: 'dave@example.com' })

    await assertFails(asUser('dave', { provider: 'password' }).ref('users/dave').set(record))
    await assertFails(asAnonymous().ref('users/dave').set(record))
  })

  it('recriar o próprio registro por cima do existente é negado', async () => {
    const record = buildUserRecord({ uid: 'alice', name: 'Alice', email: 'alice@example.com' })

    await assertFails(asUser('alice').ref('users/alice').set(record))
  })

  it('a pessoa atualiza o próprio nome e lastSeenAt', async () => {
    await assertSucceeds(asUser('alice').ref('users/alice').update({ name: 'Alice Souza', lastSeenAt: serverTimestamp() }))
  })

  it('nome fora de 1 a 80 caracteres é negado', async () => {
    await assertFails(asUser('alice').ref('users/alice/name').set(''))
    await assertFails(asUser('alice').ref('users/alice/name').set('n'.repeat(81)))
    await assertSucceeds(asUser('alice').ref('users/alice/name').set('n'.repeat(80)))
  })

  it('lastSeenAt só aceita a hora do servidor', async () => {
    await assertFails(asUser('alice').ref('users/alice/lastSeenAt').set(123))
  })

  it.each([
    ['role', 'admin'],
    ['role', 'member'],
    ['blocked', false],
    ['createdAt', 1],
    ['email', 'alice@example.com'],
    ['email', 'outro@example.com'],
  ])('a pessoa não muda o próprio %s (%j)', async (field, value) => {
    await assertFails(asUser('alice').ref(`users/alice/${field}`).set(value))
  })

  it('a pessoa não apaga o próprio registro nem campos dele', async () => {
    await assertFails(asUser('alice').ref('users/alice').remove())
    await assertFails(asUser('alice').ref('users/alice/name').remove())
    await assertFails(asUser('alice').ref('users/alice/lastSentAt').set(null))
  })

  it('sentCount não aceita valor solto do cliente', async () => {
    await assertFails(asUser('alice').ref('users/alice/sentCount').set(100))
  })

  it('campo desconhecido no registro é negado', async () => {
    await assertFails(asUser('alice').ref('users/alice/vip').set(true))
  })

  it('pessoa bloqueada não atualiza nada', async () => {
    await assertFails(asUser('carol').ref('users/carol/lastSeenAt').set(serverTimestamp()))
    await assertFails(asUser('carol').ref('users/carol/name').set('Carol 2'))
  })

  it('admin promove, rebaixa, bloqueia e desbloqueia outra pessoa', async () => {
    const bob = asUser('bob')

    await assertSucceeds(bob.ref('users/alice/role').set('admin'))
    await assertSucceeds(bob.ref('users/alice/role').set('member'))
    await assertSucceeds(bob.ref('users/alice/blocked').set(true))
    await assertSucceeds(bob.ref('users/alice/blocked').set(false))
    await assertSucceeds(bob.ref('users/carol/blocked').set(false))
  })

  it('admin não muda o próprio papel nem se bloqueia', async () => {
    await assertFails(asUser('bob').ref('users/bob/role').set('member'))
    await assertFails(asUser('bob').ref('users/bob/blocked').set(true))
  })

  it('admin não grava valor inválido em role e blocked', async () => {
    await assertFails(asUser('bob').ref('users/alice/role').set('dono'))
    await assertFails(asUser('bob').ref('users/alice/blocked').set('sim'))
    await assertFails(asUser('bob').ref('users/alice/role').remove())
  })

  it('admin não mexe em e-mail, nome ou contagem dos outros', async () => {
    await assertFails(asUser('bob').ref('users/alice/email').set('x@example.com'))
    await assertFails(asUser('bob').ref('users/alice/name').set('Nome do admin'))
    await assertFails(asUser('bob').ref('users/alice/sentCount').set(1))
  })

  it('membro não muda o papel de ninguém', async () => {
    await assertFails(asUser('alice').ref('users/carol/role').set('admin'))
    await assertFails(asUser('alice').ref('users/erin/blocked').set(true))
  })

  it('admin bloqueado não promove nem bloqueia', async () => {
    await assertFails(asUser('zed').ref('users/alice/role').set('admin'))
    await assertFails(asUser('zed').ref('users/erin/blocked').set(true))
  })

  it('leitura: admin e PC leem /users; membro não', async () => {
    await assertSucceeds(asUser('bob').ref('users').get())
    await assertSucceeds(asPc().ref('users').get())
    await assertFails(asUser('alice').ref('users').get())
    await assertFails(asUser('carol').ref('users').get())
    await assertFails(asAnonymous().ref('users').get())
  })

  it('leitura: cada um lê só o próprio registro (bloqueada também)', async () => {
    await assertSucceeds(asUser('alice').ref('users/alice').get())
    await assertFails(asUser('alice').ref('users/bob').get())
    await assertSucceeds(asUser('carol').ref('users/carol').get())
  })

  it('admin bloqueado não lê /users', async () => {
    await assertFails(asUser('zed').ref('users').get())
  })
})

describe('requests: criação', () => {
  it('membro envia com as escritas do app', async () => {
    await assertSucceeds(send(asUser('alice'), sendUpdate('r1', 'alice')))
  })

  it('o envio deixa lastSentAt, lastRequestId e sentCount no registro', async () => {
    await send(asUser('alice'), sendUpdate('r1', 'alice'))
    await send(asUser('bob'), sendUpdate('r2', 'bob'))
    await send(asUser('bob'), sendUpdate('r3', 'bob'))

    const alice = (await asUser('alice').ref('users/alice').get()).val()
    expect(alice.lastRequestId).toBe('r1')
    expect(alice.sentCount).toBe(1)
    expect(typeof alice.lastSentAt).toBe('number')
    const bob = (await asUser('bob').ref('users/bob').get()).val()
    expect(bob.sentCount).toBe(2)
    expect(bob.lastRequestId).toBe('r3')
  })

  it('segundo envio logo depois é negado; depois de 20 s passa', async () => {
    const alice = asUser('alice')
    await assertSucceeds(send(alice, sendUpdate('r1', 'alice')))
    await assertFails(send(alice, sendUpdate('r2', 'alice')))

    await seed('users/alice/lastSentAt', Date.now() - 21_000)
    await assertSucceeds(send(alice, sendUpdate('r3', 'alice')))
  })

  it('com o envio anterior há menos de 20 s ainda é negado', async () => {
    await seed('users/alice/lastSentAt', Date.now() - 15_000)
    await assertFails(send(asUser('alice'), sendUpdate('r1', 'alice')))
  })

  it('admin envia várias vezes seguidas', async () => {
    const bob = asUser('bob')

    await assertSucceeds(send(bob, sendUpdate('r1', 'bob')))
    await assertSucceeds(send(bob, sendUpdate('r2', 'bob')))
    await assertSucceeds(send(bob, sendUpdate('r3', 'bob')))
  })

  it('pedido sem lastSentAt no mesmo envio é negado', async () => {
    const request = sendUpdate('x', 'alice')['requests/x']

    await assertFails(asUser('alice').ref('requests/x').set(request))
    await assertFails(asUser('bob').ref('requests/x').set(request))
  })

  it('pedido com lastRequestId certo mas sem renovar o lastSentAt é negado (não fura o limite)', async () => {
    const full = sendUpdate('r1', 'alice')
    delete full['users/alice/lastSentAt']

    await assertFails(send(asUser('alice'), full))
    const admin = sendUpdate('r1', 'bob')
    delete admin['users/bob/lastSentAt']
    await assertFails(send(asUser('bob'), admin))

    await assertSucceeds(send(asUser('alice'), sendUpdate('r2', 'alice')))
    const spam = sendUpdate('r3', 'alice')
    delete spam['users/alice/lastSentAt']
    await assertFails(send(asUser('alice'), spam))
  })

  it('pedido com lastRequestId de outro pedido é negado', async () => {
    const update = sendUpdate('r1', 'alice')
    update['users/alice/lastRequestId'] = 'outro'

    await assertFails(send(asUser('alice'), update))
  })

  it('dois pedidos num envio só são negados (um por envio)', async () => {
    const one = sendUpdate('a', 'alice')
    const two = sendUpdate('b', 'alice')
    const update: Update = { 'requests/a': one['requests/a'], 'requests/b': two['requests/b'], ...one }

    await assertFails(send(asUser('alice'), update))
    await assertFails(send(asUser('bob'), { 'requests/a': sendUpdate('a', 'bob')['requests/a'], 'requests/b': sendUpdate('b', 'bob')['requests/b'], ...sendUpdate('a', 'bob') }))
  })

  it('raw: 300 caracteres passa, 301 e vazio não', async () => {
    await assertSucceeds(send(asUser('alice'), sendUpdate('r1', 'alice', (r) => (r.raw = 'x'.repeat(300)))))
    await assertFails(send(asUser('bob'), sendUpdate('r2', 'bob', (r) => (r.raw = 'x'.repeat(301)))))
    await assertFails(send(asUser('erin'), sendUpdate('r3', 'erin', (r) => (r.raw = ''))))
  })

  it('raw com 300 unidades de emoji (o máximo que o campo deixa) passa', async () => {
    const emoji = '\u{1F697}'.repeat(150)
    expect(emoji.length).toBe(300)

    await assertSucceeds(send(asUser('alice'), sendUpdate('r1', 'alice', (r) => (r.raw = emoji))))
  })

  it('raw com 300 letras acentuadas (o campo deixa) passa', async () => {
    await assertSucceeds(send(asUser('alice'), sendUpdate('r1', 'alice', (r) => (r.raw = 'ç'.repeat(300)))))
  })

  it('uid de outra pessoa no pedido é negado', async () => {
    await assertFails(send(asUser('alice'), sendUpdate('r1', 'alice', (r) => (r.uid = 'bob'))))
  })

  it('pessoa bloqueada e pessoa sem registro não enviam', async () => {
    await assertFails(send(asUser('carol'), sendUpdate('r1', 'carol')))
    await assertFails(send(asUser('dave'), sendUpdate('r2', 'dave')))
    await assertFails(send(asUser('zed'), sendUpdate('r3', 'zed')))
  })

  it('sem login não envia', async () => {
    await assertFails(asAnonymous().ref('requests/r1').set(sendUpdate('r1', 'alice')['requests/r1']))
  })

  it('a conta do PC não cria pedido como pessoa', async () => {
    await assertFails(send(asPc(), sendUpdate('r1', 'pc')))
  })

  it('status, cancel verdadeiro, createdAt do cliente e campo extra na criação são negados', async () => {
    const db = asUser('alice')

    await assertFails(send(db, sendUpdate('r1', 'alice', (r) => (r.status = { state: 'shown', at: 1 }))))
    await seed('users/alice/lastSentAt', null)
    await assertFails(send(db, sendUpdate('r2', 'alice', (r) => (r.cancel = true))))
    await assertFails(send(db, sendUpdate('r3', 'alice', (r) => (r.createdAt = Date.now() - 5000))))
    await assertFails(send(db, sendUpdate('r4', 'alice', (r) => (r.extra = 1))))
  })

  it('pré-visualização inválida é negada: kind, placa, by, texto grande e campo extra', async () => {
    const db = asUser('alice')
    const bad = (tweak: (p: Record<string, unknown>) => void) => (r: Record<string, unknown>) => tweak(r.preview as Record<string, unknown>)

    await assertFails(send(db, sendUpdate('r1', 'alice', bad((p) => (p.kind = 'reject')))))
    await assertFails(send(db, sendUpdate('r2', 'alice', bad((p) => (p.plate = 'ABC1234')))))
    await assertFails(send(db, sendUpdate('r3', 'alice', bad((p) => (p.by = 'humano')))))
    await assertFails(send(db, sendUpdate('r4', 'alice', bad((p) => (p.text = 'x'.repeat(401))))))
    await assertFails(send(db, sendUpdate('r5', 'alice', bad((p) => (p.extra = 1)))))
  })

  it('pré-visualização boa: Mercosul, texto de 400 caracteres e aviso livre sem placa', async () => {
    await assertSucceeds(send(asUser('bob'), sendUpdate('r1', 'bob', undefined, { preview: { ...PREVIEW, plate: 'BRA-2E19' } })))
    await assertSucceeds(send(asUser('bob'), sendUpdate('r2', 'bob', undefined, { preview: { text: 'x'.repeat(400), kind: 'free', by: 'gemini' } })))
    await assertSucceeds(send(asUser('bob'), sendUpdate('r3', 'bob', undefined, { preview: null })))
  })

  it('nome do pedido fora de 1 a 80 caracteres é negado', async () => {
    await assertFails(send(asUser('bob'), sendUpdate('r1', 'bob', (r) => (r.name = ''))))
    await assertFails(send(asUser('bob'), sendUpdate('r2', 'bob', (r) => (r.name = 'n'.repeat(81)))))
  })

  it('urgente tem de ser booleano', async () => {
    await assertFails(send(asUser('bob'), sendUpdate('r1', 'bob', (r) => (r.urgent = 'sim'))))
  })

  it('pedido já gravado não pode ser regravado por cima', async () => {
    await seedRequest('r1', 'alice', { state: 'queued', position: 1, at: Date.now() })
    await seed('users/alice/lastSentAt', Date.now() - 60_000)

    await assertFails(send(asUser('alice'), sendUpdate('r1', 'alice')))
  })
})

describe('requests: depois de criado', () => {
  it('a pessoa não muda raw, uid, preview nem urgente do pedido', async () => {
    await seedRequest('r1', 'alice')
    const db = asUser('alice')

    await assertFails(db.ref('requests/r1/raw').set('outra coisa'))
    await assertFails(db.ref('requests/r1/uid').set('bob'))
    await assertFails(db.ref('requests/r1/urgent').set(true))
    await assertFails(db.ref('requests/r1/preview').set(PREVIEW))
  })

  it('dono cancela o próprio pedido sem status ou pendente ou na fila', async () => {
    await seedRequest('r1', 'alice')
    await seedRequest('r2', 'alice', { state: 'pending', at: Date.now() })
    await seedRequest('r3', 'alice', { state: 'queued', position: 1, at: Date.now() })
    const db = asUser('alice')

    await assertSucceeds(db.ref('requests/r1/cancel').set(true))
    await assertSucceeds(db.ref('requests/r2/cancel').set(true))
    await assertSucceeds(db.ref('requests/r3/cancel').set(true))
  })

  it('dono não cancela o que já está passando, passou, foi recusado, cancelado ou expirou', async () => {
    const db = asUser('alice')
    for (const state of ['showing', 'shown', 'rejected', 'cancelled', 'expired']) {
      await seedRequest(`r-${state}`, 'alice', { state, at: Date.now() })

      await assertFails(db.ref(`requests/r-${state}/cancel`).set(true))
    }
  })

  it('cancel nunca volta para false nem recebe valor que não é true', async () => {
    await seedRequest('r1', 'alice', undefined, true)
    await seedRequest('r2', 'alice')

    await assertFails(asUser('alice').ref('requests/r1/cancel').set(false))
    await assertFails(asUser('bob').ref('requests/r1/cancel').set(false))
    await assertFails(asUser('alice').ref('requests/r2/cancel').set('sim'))
    await assertFails(asUser('alice').ref('requests/r2/cancel').remove())
  })

  it('outra pessoa não cancela o pedido alheio', async () => {
    await seedRequest('r1', 'alice')

    await assertFails(asUser('erin').ref('requests/r1/cancel').set(true))
    await assertFails(asUser('dave').ref('requests/r1/cancel').set(true))
  })

  it('pessoa bloqueada não cancela nem o próprio', async () => {
    await seedRequest('r1', 'carol')

    await assertFails(asUser('carol').ref('requests/r1/cancel').set(true))
  })

  it('admin cancela o de qualquer um, até passando; admin bloqueado não', async () => {
    await seedRequest('r1', 'alice', { state: 'showing', at: Date.now() })
    await seedRequest('r2', 'alice')

    await assertSucceeds(asUser('bob').ref('requests/r1/cancel').set(true))
    await assertFails(asUser('zed').ref('requests/r2/cancel').set(true))
  })

  it('status: só o PC escreve, e só com valores da spec', async () => {
    await seedRequest('r1', 'alice')

    await assertFails(asUser('alice').ref('requests/r1/status').set({ state: 'shown', at: Date.now() }))
    await assertFails(asUser('bob').ref('requests/r1/status').set({ state: 'shown', at: Date.now() }))
    await assertSucceeds(asPc().ref('requests/r1/status').set({ state: 'queued', position: 1, at: Date.now() }))
    await assertFails(asPc().ref('requests/r1/status').set({ state: 'done', at: Date.now() }))
    await assertFails(asPc().ref('requests/r1/status').set({ state: 'queued', position: 0, at: Date.now() }))
    await assertFails(asPc().ref('requests/r1/status').set({ state: 'queued', at: Date.now(), extra: 1 }))
    await assertFails(asPc().ref('requests/r1/status').set({ state: 'rejected', reason: 'x'.repeat(201), at: Date.now() }))
    await assertSucceeds(
      asPc().ref('requests/r1/status').set({ state: 'rejected', reason: 'faltou placa', text: 'GOL BRANCO', at: Date.now() }),
    )
  })

  it('o PC muda o status sem esbarrar nas validações do resto do pedido', async () => {
    await seedRequest('r1', 'alice')
    const pc = asPc()

    await assertSucceeds(pc.ref('requests/r1/status').set({ state: 'showing', at: Date.now() }))
    await assertSucceeds(pc.ref('requests/r1/status/state').set('shown'))
    await assertSucceeds(pc.ref('requests/r1/status').update({ text: 'GOL BRANCO' }))
  })

  it('apagar: o PC apaga, a pessoa não apaga o próprio', async () => {
    await seedRequest('r1', 'alice')
    await seedRequest('r2', 'alice')

    await assertFails(asUser('alice').ref('requests/r1').remove())
    await assertFails(asUser('bob').ref('requests/r1').remove())
    await assertSucceeds(asPc().ref('requests/r1').remove())
    await assertSucceeds(asPc().ref('requests/r2').remove())
  })

  it('o PC não cria pedido novo nem muda os dados dele', async () => {
    await seedRequest('r1', 'alice')

    await assertFails(asPc().ref('requests/r9').set({ uid: 'pc', name: 'pc', raw: 'x', urgent: false, createdAt: Date.now(), cancel: false }))
    await assertFails(asPc().ref('requests/r1/raw').set('mudou'))
  })
})

describe('requests: leitura', () => {
  it('a pessoa lê os próprios pedidos por consulta orderByChild(uid).equalTo(uid)', async () => {
    await seedRequest('r1', 'alice')
    await seedRequest('r2', 'erin')

    const snap = await assertSucceeds(asUser('alice').ref('requests').orderByChild('uid').equalTo('alice').get())
    expect(Object.keys(snap.val())).toEqual(['r1'])
  })

  it('sem consulta, ou com consulta dos pedidos de outro, é negado', async () => {
    await seedRequest('r1', 'alice')

    await assertFails(asUser('alice').ref('requests').get())
    await assertFails(asUser('alice').ref('requests').orderByChild('uid').equalTo('bob').get())
    await assertFails(asUser('alice').ref('requests').orderByChild('createdAt').get())
  })

  it('a pessoa lê um pedido seu direto, mas não o alheio', async () => {
    await seedRequest('r1', 'alice')

    await assertSucceeds(asUser('alice').ref('requests/r1').get())
    await assertFails(asUser('erin').ref('requests/r1').get())
  })

  it('admin e PC leem tudo', async () => {
    await seedRequest('r1', 'alice')

    await assertSucceeds(asUser('bob').ref('requests').get())
    await assertSucceeds(asPc().ref('requests').get())
  })

  it('pessoa bloqueada não lê nem a própria consulta; sem login não lê', async () => {
    await seedRequest('r1', 'carol')

    await assertFails(asUser('carol').ref('requests').orderByChild('uid').equalTo('carol').get())
    await assertFails(asAnonymous().ref('requests').get())
    await assertFails(asUser('dave').ref('requests').orderByChild('uid').equalTo('dave').get())
  })
})

describe('live', () => {
  it('quem está ativo lê; bloqueado, sem registro e sem login não', async () => {
    await seed('live', { heartbeatAt: Date.now(), led: true })

    await assertSucceeds(asUser('alice').ref('live').get())
    await assertSucceeds(asUser('bob').ref('live').get())
    await assertSucceeds(asPc().ref('live').get())
    await assertFails(asUser('carol').ref('live').get())
    await assertFails(asUser('dave').ref('live').get())
    await assertFails(asAnonymous().ref('live').get())
  })

  it('só o PC escreve', async () => {
    await assertFails(asUser('alice').ref('live').set({ led: true }))
    await assertFails(asUser('bob').ref('live').set({ led: true }))
    await assertFails(asUser('bob').ref('live/led').set(true))
    await assertFails(asAnonymous().ref('live').set({ led: true }))
    await assertSucceeds(asPc().ref('live').set({ heartbeatAt: Date.now(), led: true, paused: false }))
    await assertSucceeds(asPc().ref('live/heartbeatAt').set(Date.now()))
  })
})

describe('devices', () => {
  it('o PC lê o próprio registro; membro e outros não leem', async () => {
    await assertSucceeds(asPc().ref('devices/pc').get())
    await assertFails(asUser('alice').ref('devices/pc').get())
    await assertFails(asUser('bob').ref('devices').get())
    await assertFails(asAnonymous().ref('devices/pc').get())
  })

  it('ninguém escreve em /devices pelo cliente, nem o PC nem admin', async () => {
    await assertFails(asPc().ref('devices/pc').set(true))
    await assertFails(asUser('bob').ref('devices/bob').set(true))
    await assertFails(asUser('alice').ref('devices/alice').set(true))
  })

  it('uma conta de senha que não é o PC não ganha poderes de PC', async () => {
    await assertFails(asUser('intruso', { provider: 'password' }).ref('live').set({ led: true }))
    await assertFails(asUser('intruso', { provider: 'password' }).ref('users').get())
    await assertFails(asUser('intruso', { provider: 'password' }).ref('requests').get())
  })
})

describe('raiz', () => {
  it('nó desconhecido na raiz é negado até para o admin e o PC', async () => {
    await assertFails(asUser('bob').ref('qualquercoisa').set(1))
    await assertFails(asPc().ref('qualquercoisa').set(1))
    await assertFails(asUser('bob').ref().get())
  })
})
