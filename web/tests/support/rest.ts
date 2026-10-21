import { PROJECT_ID } from './rulesEnv'

// Acesso direto ao emulador do Realtime Database (sem passar pelas regras): faz as vezes do PC e da CLI nos
// testes. `Authorization: Bearer owner` é a conta de dono do emulador.

export const DB_HOST = '127.0.0.1:9000'
export const NAMESPACE = `${PROJECT_ID}-default-rtdb`

export function dbUrl(path: string): string {
  return `http://${DB_HOST}/${path.replace(/^\/+/, '')}.json?ns=${NAMESPACE}`
}

async function call(method: string, path: string, value?: unknown): Promise<unknown> {
  const res = await fetch(dbUrl(path), {
    method,
    headers: { Authorization: 'Bearer owner', 'Content-Type': 'application/json' },
    body: value === undefined ? undefined : JSON.stringify(value),
  })
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${await res.text()}`)
  return res.json()
}

export const restPut = (path: string, value: unknown) => call('PUT', path, value)
export const restPatch = (path: string, value: unknown) => call('PATCH', path, value)
export const restGet = (path: string) => call('GET', path)
export const restDelete = (path: string) => call('DELETE', path)

/** Apaga todas as contas do emulador do Auth. */
export async function clearAuthAccounts(): Promise<void> {
  await fetch(`http://127.0.0.1:9099/emulator/v1/projects/${PROJECT_ID}/accounts`, { method: 'DELETE' })
}
