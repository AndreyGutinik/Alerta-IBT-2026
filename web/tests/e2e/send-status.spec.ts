import { type Browser, devices, expect, type Page, test } from '@playwright/test'
import { clearAuthAccounts, restDelete, restGet, restPatch, restPut } from '../support/rest'

// Do login ao bloqueio, com o app de verdade (modo emulator) e os emuladores de Auth e Database. O "PC" é este
// teste escrevendo `status` e `/live` pelo REST do emulador (conta de dono), como o conector do Plano 2B fará.

const SERVER_TIME = { '.sv': 'timestamp' }
const BAR = { position: 'top', height: 56, speed: 90 }
const PREVIEW_TEXT = 'COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO'

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

/** O PC publica `/live` (e o heartbeat); `extra` troca o que mudou. */
async function pcLive(extra: Record<string, unknown> = {}): Promise<void> {
  await restPatch('live', { heartbeatAt: SERVER_TIME, led: true, paused: false, bar: BAR, ...extra })
}

async function pcStatus(id: string, status: Record<string, unknown>): Promise<void> {
  await restPut(`requests/${id}/status`, { at: SERVER_TIME, ...status })
}

async function onlyRequestId(): Promise<string> {
  const requests = (await restGet('requests')) as Record<string, unknown> | null
  const ids = Object.keys(requests ?? {})
  expect(ids).toHaveLength(1)
  return ids[0]
}

async function newestRequestId(): Promise<string> {
  const requests = (await restGet('requests')) as Record<string, { createdAt: number }>
  return Object.entries(requests).sort(([, a], [, b]) => b.createdAt - a.createdAt)[0][0]
}

async function uidOf(email: string): Promise<string> {
  const users = (await restGet('users')) as Record<string, { email: string }>
  const found = Object.entries(users).find(([, u]) => u.email === email)
  expect(found, `usuário ${email} em /users`).toBeTruthy()
  return found![0]
}

/** Abre o app e entra com uma conta Google falsa do emulador. */
async function signIn(page: Page, email: string, name: string): Promise<void> {
  await page.goto('/')
  await page.waitForFunction(() => 'signIn' in ((window as unknown as { __alertaE2E?: object }).__alertaE2E ?? {}))
  await page.evaluate(([e, n]) => (window as unknown as { __alertaE2E: { signIn(e: string, n: string): Promise<void> } }).__alertaE2E.signIn(e, n), [email, name])
}

async function newPhone(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ ...devices['Pixel 7'] })
  return context.newPage()
}

const myAlerts = (page: Page) => page.locator('section[aria-labelledby="my-alerts-title"]')
const headerPill = (page: Page) => page.locator('.ab .pill')

test.beforeEach(async () => {
  await restDelete('')
  await clearAuthAccounts()
  await restPut('live', { heartbeatAt: SERVER_TIME, led: true, paused: false, bar: BAR })
})

test('entrar, enviar, o PC responde, cancelar, bloquear como admin e a tela de bloqueado', async ({ browser, page }) => {
  const errors: string[] = []
  page.on('pageerror', (e) => errors.push(e.message))

  // 1. Maria entra (Google falso) e pula o tutorial.
  await signIn(page, 'maria@example.com', 'Maria Souza')
  await page.getByRole('button', { name: 'Pular' }).click()
  await expect(headerPill(page)).toHaveText('Telão ligado')

  // 2. Escreve o aviso: a prévia sai do parser local (sem IA no emulador) e Enviar acende.
  const field = page.getByPlaceholder('Ex.: corolla prata abc1d23 vidro aberto')
  await field.fill('prata corolla bra2e19 vidro aberto')
  await expect(page.locator('.pre')).toContainText(PREVIEW_TEXT)
  await page.getByRole('button', { name: 'Enviar' }).click()
  await expect(page.getByText('Enviado', { exact: true })).toBeVisible()
  await expect(myAlerts(page).locator('li')).toHaveCount(1)
  await expect(myAlerts(page).locator('li').first()).toContainText('enviado')

  const first = await onlyRequestId()
  expect(((await restGet(`requests/${first}`)) as { raw: string }).raw).toBe('prata corolla bra2e19 vidro aberto')

  // 3. O PC enfileira: "1º na fila".
  await pcStatus(first, { state: 'queued', position: 1 })
  await pcLive({ queue: [{ id: first, text: PREVIEW_TEXT, source: 'site', plate: 'BRA-2E19' }] })
  await expect(myAlerts(page).locator('li').first()).toContainText('1º na fila')

  // 4. O PC põe no telão: "passando", e a aba Status mostra o mini-telão e o card.
  await pcStatus(first, { state: 'showing' })
  await pcLive({ queue: null, now: { id: first, text: PREVIEW_TEXT, source: 'site', startedAt: SERVER_TIME } })
  await expect(myAlerts(page).locator('li').first()).toContainText('passando')
  await page.getByRole('tab', { name: 'Status' }).click()
  await expect(page.getByRole('heading', { name: 'Agora' })).toBeVisible()
  await expect(headerPill(page)).toHaveText('PC online')
  await expect(page.getByTestId('mini-led').locator('.tick')).toHaveText(PREVIEW_TEXT)
  await expect(page.locator('.msg')).toHaveText(PREVIEW_TEXT)
  await expect(page.getByText('LED ligado')).toBeVisible()
  await expect(page.getByText('Barra ativa')).toBeVisible()

  // 5. Passou: o item de "Seus avisos" mostra a hora.
  await pcStatus(first, { state: 'shown' })
  await pcLive({ now: null, recent: [{ id: first, text: PREVIEW_TEXT, state: 'shown', at: SERVER_TIME, plate: 'BRA-2E19' }] })
  await expect(page.getByText('Nenhum aviso no telão agora.')).toBeVisible()
  await page.getByRole('tab', { name: 'Enviar' }).click()
  await expect(myAlerts(page).locator('li').first().locator('.st')).toHaveText(/^\d{2}:\d{2}$/)

  // 6. Outro aviso (a CLI/teste recua o relógio do limite de 20 s) e o cancelamento pelo X.
  await restPatch(`users/${await uidOf('maria@example.com')}`, { lastSentAt: Date.now() - 60_000 })
  await field.fill('onix preto qwe1b23 farol aceso')
  await expect(page.locator('.pre')).toContainText('ONIX PRETO | PLACA: QWE-1B23 | FAROL ACESO')
  await page.getByRole('button', { name: 'Enviar' }).click()
  await expect(myAlerts(page).locator('li')).toHaveCount(2)
  const second = await newestRequestId()
  expect(second).not.toBe(first)
  await myAlerts(page).locator('li').filter({ hasText: 'ONIX PRETO' }).getByRole('button', { name: 'Cancelar aviso' }).click()
  await expect.poll(async () => restGet(`requests/${second}/cancel`)).toBe(true)
  await expect(myAlerts(page).locator('li').filter({ hasText: 'ONIX PRETO' })).toContainText('cancelando')
  await pcStatus(second, { state: 'cancelled', reason: 'pedido do autor' })
  await expect(myAlerts(page).locator('li').filter({ hasText: 'ONIX PRETO' })).toContainText('cancelado: pedido do autor')

  // 7. Um admin (outro celular) abre a Equipe e remove a Maria.
  const adminPage = await newPhone(browser)
  await signIn(adminPage, 'admin@example.com', 'Admin Silva')
  await adminPage.getByRole('button', { name: 'Pular' }).click()
  await expect(adminPage.getByRole('tab', { name: 'Equipe' })).toHaveCount(0) // ainda é membro
  await restPatch(`users/${await uidOf('admin@example.com')}`, { role: 'admin' }) // a CLI da instalação promove o primeiro admin
  await adminPage.getByRole('tab', { name: 'Equipe' }).click()
  await expect(adminPage.getByRole('heading', { name: 'Equipe' })).toBeVisible()
  const mariaRow = adminPage.locator('li.mem').filter({ hasText: 'maria@example.com' })
  await expect(mariaRow).toContainText('2 avisos')
  await expect(adminPage.locator('li.mem').filter({ hasText: 'admin@example.com' })).toContainText('você')
  await mariaRow.getByRole('button', { name: 'Remover' }).click()

  // 8. A Maria cai na tela de bloqueado na hora, sem recarregar.
  await expect(page.getByRole('heading', { name: 'Seu acesso foi removido' })).toBeVisible()
  await expect(mariaRow).toContainText('removido')

  // 9. Restaurar devolve o app.
  await mariaRow.getByRole('button', { name: 'Restaurar' }).click()
  await expect(page.getByRole('tab', { name: 'Enviar' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Seu acesso foi removido' })).toHaveCount(0)

  await sleep(100)
  expect(errors).toEqual([])
})

test('PC sem heartbeat há mais de 90 s: pílula PC offline e o aviso velho some do mini-telão', async ({ page }) => {
  await signIn(page, 'joao@example.com', 'João Lima')
  await page.getByRole('button', { name: 'Pular' }).click()
  await pcLive({ now: { id: 'x', text: 'FOX PRATA | PLACA: ETC-6785 | VIDRO ABERTO', source: 'telegram', startedAt: SERVER_TIME } })
  await page.getByRole('tab', { name: 'Status' }).click()
  await expect(page.locator('.msg')).toHaveText('FOX PRATA | PLACA: ETC-6785 | VIDRO ABERTO')

  await restPatch('live', { heartbeatAt: Date.now() - 120_000 })
  await expect(headerPill(page)).toHaveText('PC offline')
  await expect(page.getByText('FOX PRATA')).toHaveCount(0)
  await expect(page.getByText('PC offline. Sem informação do telão agora.')).toBeVisible()
})

test('sem sinal: enviar dá erro na hora e nada fica guardado para depois', async ({ page, context }) => {
  await signIn(page, 'ana@example.com', 'Ana Paula')
  await page.getByRole('button', { name: 'Pular' }).click()
  await expect(headerPill(page)).toHaveText('Telão ligado')
  await page.getByPlaceholder('Ex.: corolla prata abc1d23 vidro aberto').fill('gol branco ffo4745 vidro aberto')
  await expect(page.locator('.pre')).toContainText('GOL BRANCO')

  // O celular perde o sinal: o socket do banco cai e o app percebe.
  await context.setOffline(true)
  await expect(headerPill(page)).toHaveText('Sem conexão', { timeout: 30_000 })
  await expect(page.getByRole('button', { name: 'Enviar' })).toBeEnabled()
  await page.getByRole('button', { name: 'Enviar' }).click()
  await expect(page.getByText('Sem conexão. Confira a internet e tente de novo.')).toBeVisible()
  await expect(page.getByPlaceholder('Ex.: corolla prata abc1d23 vidro aberto')).toHaveValue('gol branco ffo4745 vidro aberto')

  // O sinal volta: nada foi guardado e nada chega sozinho.
  await context.setOffline(false)
  await expect(headerPill(page)).toHaveText('Telão ligado', { timeout: 30_000 })
  await sleep(1000)
  expect(await restGet('requests')).toBeNull()
})
