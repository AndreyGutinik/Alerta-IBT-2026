import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import {
  type RulesTestEnvironment,
  initializeTestEnvironment,
} from '@firebase/rules-unit-testing'
import { beforeAll, beforeEach, afterAll } from 'vitest'

// Projeto `demo-*`: o emulador nunca fala com a nuvem.
export const PROJECT_ID = 'demo-alertaibt'

const RULES_PATH = resolve(import.meta.dirname, '../../../database.rules.json')

let env: RulesTestEnvironment | null = null

export function testEnv(): RulesTestEnvironment {
  if (!env) throw new Error('rulesEnv: o ambiente ainda não foi iniciado (use setupRulesEnv no topo do teste).')
  return env
}

export interface Identity {
  email?: string
  provider?: string
}

/** Banco de quem está logado (como o app vê). `provider` vira `auth.token.firebase.sign_in_provider`. */
export function asUser(uid: string, { email = `${uid}@example.com`, provider = 'google.com' }: Identity = {}) {
  return testEnv()
    .authenticatedContext(uid, { email, firebase: { sign_in_provider: provider as 'google.com' } })
    .database()
}

/** Banco de quem não entrou. */
export function asAnonymous() {
  return testEnv().unauthenticatedContext().database()
}

/** A conta do PC: e-mail e senha, registrada em `/devices/pc`. */
export function asPc() {
  return asUser('pc', { provider: 'password' })
}

/** Grava sem passar pelas regras (semente dos testes). */
export async function seed(path: string, value: unknown): Promise<void> {
  await testEnv().withSecurityRulesDisabled(async (context) => {
    await context.database().ref(path).set(value)
  })
}

const NOW = () => Date.now()

function userRecord(name: string, role: 'member' | 'admin', blocked = false) {
  return {
    name,
    email: `${name.toLowerCase()}@example.com`,
    role,
    blocked,
    createdAt: NOW() - 86_400_000,
    lastSeenAt: NOW() - 3_600_000,
  }
}

/** Sementes de cada teste: alice (membro), bob (admin), carol (membro bloqueada), erin (outra membro),
 * zed (admin bloqueado) e a conta do PC. Dave não tem registro de propósito. */
export async function seedBase(): Promise<void> {
  await seed('users/alice', userRecord('alice', 'member'))
  await seed('users/bob', userRecord('bob', 'admin'))
  await seed('users/carol', userRecord('carol', 'member', true))
  await seed('users/erin', userRecord('erin', 'member'))
  await seed('users/zed', userRecord('zed', 'admin', true))
  await seed('devices/pc', true)
}

/** Liga o ambiente do emulador ao ciclo do Vitest: sobe uma vez e limpa + semeia antes de cada teste. */
export function setupRulesEnv(): void {
  beforeAll(async () => {
    env = await initializeTestEnvironment({
      projectId: PROJECT_ID,
      database: { rules: readFileSync(RULES_PATH, 'utf8') },
    })
  })
  beforeEach(async () => {
    await testEnv().clearDatabase()
    await seedBase()
  })
  afterAll(async () => {
    await env?.cleanup()
    env = null
  })
}
