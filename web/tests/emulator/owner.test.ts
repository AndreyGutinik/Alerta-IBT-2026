import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { assertFails, assertSucceeds } from '@firebase/rules-unit-testing'
import { serverTimestamp } from 'firebase/database'
import { describe, expect, it } from 'vitest'
import { buildUserRecord } from '../../src/data/writes'
import { OWNER_EMAIL } from '../../src/session/owner'
import { asUser, seed, setupRulesEnv } from '../support/rulesEnv'

setupRulesEnv()

// O mantenedor (OWNER_EMAIL): as regras (database.rules.json) não deixam ninguém, nem outro admin, rebaixar, bloquear
// ou apagar o registro dele, e o e-mail do registro só pode ser o da conta Google que o criou.
// O arquivo de regras é JSON puro (sem comentários) e não tem constantes: o e-mail aparece uma vez em "role" e uma em
// "blocked". O primeiro teste abaixo falha se essas cópias, a do site (src/session/owner.ts) e a do PC divergirem.
const ROOT = resolve(import.meta.dirname, '../../..')
const OWNER_UID = 'owner'

function record(email: string, role: 'member' | 'admin', blocked = false) {
  return { name: 'Dono', email, role, blocked, createdAt: Date.now() - 86_400_000, lastSeenAt: Date.now() - 1000 }
}

const seedOwner = (role: 'member' | 'admin' = 'admin') => seed(`users/${OWNER_UID}`, record(OWNER_EMAIL, role))
const asOwner = () => asUser(OWNER_UID, { email: OWNER_EMAIL })

describe('as cópias do e-mail do mantenedor', () => {
  const rules = readFileSync(resolve(ROOT, 'database.rules.json'), 'utf8')

  it('as regras repetem o mesmo e-mail em role e em blocked, e é o OWNER_EMAIL do site', () => {
    const tree = JSON.parse(rules).rules.users.$uid
    const emailsIn = (rule: string) =>
      [...rule.matchAll(/matches\(\/\^([^/]+?)\$\/\)/g)]
        .map((m) => m[1].replaceAll('\\.', '.'))
        .filter((email) => email.includes('@'))

    expect(emailsIn(tree.role['.write'])).toEqual([OWNER_EMAIL])
    expect(emailsIn(tree.blocked['.write'])).toEqual([OWNER_EMAIL])
    // nenhum outro e-mail solto no arquivo: só essas duas cópias
    expect(rules.match(/protonmail/g)).toHaveLength(2)
  })

  it('o PC tem o mesmo e-mail em SiteContract.ProtectedAdmins', () => {
    const source = readFileSync(resolve(ROOT, 'desktop/src/AlertaIBT.Core/Firebase/SiteContract.cs'), 'utf8')

    expect(source.toLowerCase()).toContain(`"${OWNER_EMAIL}"`)
  })
})

describe('mantenedor: ninguém o rebaixa, bloqueia ou apaga', () => {
  it('outro admin não rebaixa nem bloqueia o mantenedor', async () => {
    await seedOwner()
    const bob = asUser('bob')

    await assertFails(bob.ref(`users/${OWNER_UID}/role`).set('member'))
    await assertFails(bob.ref(`users/${OWNER_UID}/blocked`).set(true))
    await assertFails(bob.ref(`users/${OWNER_UID}`).update({ role: 'member', blocked: true }))
    await assertFails(bob.ref().update({ [`users/${OWNER_UID}/role`]: 'member' }))
    expect((await asOwner().ref(`users/${OWNER_UID}`).get()).val()).toMatchObject({ role: 'admin', blocked: false })
  })

  it('vale para o e-mail em outra caixa (como pode estar salvo no registro)', async () => {
    await seed('users/owner2', record('GutnikAndrey01@ProtonMail.com', 'admin'))

    await assertFails(asUser('bob').ref('users/owner2/role').set('member'))
    await assertFails(asUser('bob').ref('users/owner2/blocked').set(true))
  })

  it('o mantenedor que ainda é membro no banco só é promovido pela CLI, não pelo site', async () => {
    await seedOwner('member')

    await assertFails(asUser('bob').ref(`users/${OWNER_UID}/role`).set('admin'))
    await assertFails(asUser('bob').ref(`users/${OWNER_UID}/blocked`).set(true))
  })

  it('ninguém apaga nem troca o registro inteiro do mantenedor', async () => {
    await seedOwner()
    const fresh = buildUserRecord({ uid: OWNER_UID, name: 'Dono', email: OWNER_EMAIL })
    const owner = asOwner()

    await assertFails(asUser('bob').ref(`users/${OWNER_UID}`).remove())
    await assertFails(asUser('bob').ref(`users/${OWNER_UID}`).set(fresh))
    await assertFails(asUser('bob').ref().update({ [`users/${OWNER_UID}`]: null }))
    await assertFails(asUser('alice').ref(`users/${OWNER_UID}`).remove())
    await assertFails(owner.ref(`users/${OWNER_UID}`).remove())
    await assertFails(owner.ref(`users/${OWNER_UID}`).set(fresh))
    await assertFails(owner.ref(`users/${OWNER_UID}/role`).set('member'))
    await assertFails(owner.ref(`users/${OWNER_UID}/blocked`).set(true))
  })

  it('o mantenedor continua usando o app: nome e lastSeenAt', async () => {
    await seedOwner()

    await assertSucceeds(asOwner().ref(`users/${OWNER_UID}`).update({ name: 'Mantenedor', lastSeenAt: serverTimestamp() }))
  })

  it('o mantenedor administra os outros normalmente', async () => {
    await seedOwner()
    const owner = asOwner()

    await assertSucceeds(owner.ref('users/alice/role').set('admin'))
    await assertSucceeds(owner.ref('users/alice/role').set('member'))
    await assertSucceeds(owner.ref('users/alice/blocked').set(true))
    await assertSucceeds(owner.ref('users/alice/blocked').set(false))
  })

  it('um membro comum continua podendo ser promovido, rebaixado e bloqueado por outro admin', async () => {
    await seedOwner()
    const bob = asUser('bob')

    await assertSucceeds(bob.ref('users/erin/role').set('admin'))
    await assertSucceeds(bob.ref('users/erin/role').set('member'))
    await assertSucceeds(bob.ref('users/erin/blocked').set(true))
    await assertSucceeds(bob.ref('users/erin/blocked').set(false))
    await assertSucceeds(bob.ref('users/zed/blocked').set(false))
  })
})

describe('registro: o e-mail é o da conta Google que o criou', () => {
  it('o primeiro login do mantenedor cria o registro como member, como antes', async () => {
    const created = buildUserRecord({ uid: OWNER_UID, name: 'Dono', email: OWNER_EMAIL })

    expect(created.role).toBe('member')
    await assertSucceeds(asOwner().ref(`users/${OWNER_UID}`).set(created))
  })

  it('outra conta não cria registro com o e-mail do mantenedor', async () => {
    const spoof = buildUserRecord({ uid: 'dave', name: 'Dave', email: OWNER_EMAIL })
    const dave = asUser('dave', { email: 'dave@example.com' })

    await assertFails(dave.ref('users/dave').set(spoof))
    await assertFails(dave.ref('users/dave').set({ ...spoof, email: OWNER_EMAIL.toUpperCase() }))
    expect((await asUser('dave').ref('users/dave').get()).exists()).toBe(false)
  })

  it('e-mail com outra caixa que o do token é aceito (é o mesmo e-mail)', async () => {
    const created = buildUserRecord({ uid: 'dave', name: 'Dave', email: 'Dave@Example.com' })

    await assertSucceeds(asUser('dave', { email: 'dave@example.com' }).ref('users/dave').set(created))
  })

  it('o dono verdadeiro, com o e-mail em outra caixa no registro, também cria o seu', async () => {
    const created = buildUserRecord({ uid: OWNER_UID, name: 'Dono', email: OWNER_EMAIL.toUpperCase() })

    await assertSucceeds(asOwner().ref(`users/${OWNER_UID}`).set(created))
  })

  it('depois de criado, ninguém muda o e-mail do registro (nem para o do mantenedor)', async () => {
    await assertFails(asUser('alice').ref('users/alice/email').set(OWNER_EMAIL))
    await assertFails(asUser('alice', { email: OWNER_EMAIL }).ref('users/alice/email').set(OWNER_EMAIL))
    await assertFails(asUser('bob').ref('users/alice/email').set(OWNER_EMAIL))
    await assertFails(asUser('alice').ref('users/alice').update({ email: OWNER_EMAIL }))
    await assertFails(asUser('alice').ref('users/alice/email').remove())
  })
})
