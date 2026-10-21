import { expect, type Page, test } from '@playwright/test'
import { clearAuthAccounts, restDelete, restPut } from '../support/rest'

// O pacote de produção (service worker de verdade) em `vite preview`, modo emulator.
test.use({ baseURL: 'http://127.0.0.1:5175' })

/** Tudo o que o service worker guardou, como "origem + caminho". */
async function cachedUrls(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    const urls: string[] = []
    for (const name of await caches.keys()) {
      const cache = await caches.open(name)
      for (const request of await cache.keys()) urls.push(request.url)
    }
    return urls
  })
}

test.beforeEach(async () => {
  await restDelete('')
  await clearAuthAccounts()
  await restPut('live', {
    heartbeatAt: { '.sv': 'timestamp' },
    led: true,
    paused: false,
    bar: { position: 'top', height: 56, speed: 90 },
  })
})

test('manifesto instalável, service worker ativo, casca offline e nada do banco no cache', async ({ page, context }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Entrar com Google' })).toBeVisible()

  // Manifesto e ícones que o navegador exige para instalar.
  const manifestHref = await page.locator('link[rel="manifest"]').getAttribute('href')
  const manifest = await (await page.request.get(manifestHref!)).json()
  expect(manifest).toMatchObject({ name: 'Alerta IBT', lang: 'pt-BR', display: 'standalone', start_url: '/' })
  for (const icon of manifest.icons as { src: string; type: string }[]) {
    const res = await page.request.get(`/${icon.src}`)
    expect(res.ok(), icon.src).toBe(true)
    expect(res.headers()['content-type']).toContain(icon.type)
  }

  // O service worker assume a página.
  await page.evaluate(() => navigator.serviceWorker.ready)
  await page.reload()
  await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller !== null)).toBe(true)

  // Entra e deixa o /live chegar: o tráfego do banco e do Auth não pode parar no cache.
  await page.waitForFunction(() => 'signIn' in ((window as unknown as { __alertaE2E?: object }).__alertaE2E ?? {}))
  await page.evaluate(() =>
    (window as unknown as { __alertaE2E: { signIn(e: string, n: string): Promise<void> } }).__alertaE2E.signIn(
      'pwa@example.com',
      'Pessoa PWA',
    ),
  )
  await page.getByRole('button', { name: 'Pular' }).click()
  await expect(page.locator('.ab .pill')).toHaveText('Telão ligado')
  const urls = await cachedUrls(page)
  expect(urls.length).toBeGreaterThan(10)
  for (const url of urls) {
    const { origin } = new URL(url)
    expect(origin, url).toBe('http://127.0.0.1:5175')
    expect(url).not.toMatch(/\/live|:9000|:9099|identitytoolkit|securetoken|firebaseio|googleapis/)
  }
  // (os arquivos do pré-cache trazem ?__WB_REVISION__= no fim)
  expect(urls.some((u) => u.includes('/index.html'))).toBe(true)
  expect(urls.some((u) => u.includes('.ttf'))).toBe(true)

  // Sem rede o app ainda abre do cache (a casca), já com a sessão guardada no aparelho.
  await context.setOffline(true)
  await page.reload()
  await expect(page.locator('.app, .splash')).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Enviar' })).toBeVisible({ timeout: 20_000 })
  await expect(page.locator('.ab .pill')).toHaveText('Sem conexão', { timeout: 30_000 })
})
