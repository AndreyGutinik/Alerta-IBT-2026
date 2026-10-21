import { defineConfig, devices } from '@playwright/test'

// Teste de ponta a ponta contra os emuladores (`npm run test:e2e`): o app roda em modo `emulator`
// (.env.emulator, projeto demo-alertaibt) e nada fala com a nuvem. Usa o Chrome instalado no PC; sem Chrome,
// `PW_CHANNEL= npx playwright install chromium` e rode com `PW_CHANNEL=` vazio para usar o Chromium do Playwright.
const channel = process.env.PW_CHANNEL === undefined ? 'chrome' : process.env.PW_CHANNEL || undefined

export default defineConfig({
  testDir: './tests/e2e',
  testMatch: '**/*.spec.ts',
  timeout: 90_000,
  expect: { timeout: 8_000 },
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  use: {
    ...devices['Pixel 7'],
    channel,
    baseURL: 'http://127.0.0.1:5174',
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: 'npx vite --mode emulator --port 5174 --strictPort --host 127.0.0.1',
      url: 'http://127.0.0.1:5174',
      reuseExistingServer: false,
      timeout: 60_000,
    },
    {
      // O pacote de produção (com service worker), em modo emulator, para o teste do PWA instalado/offline.
      command: 'npx vite build --mode emulator --outDir dist-e2e && npx vite preview --outDir dist-e2e --port 5175 --strictPort --host 127.0.0.1',
      url: 'http://127.0.0.1:5175',
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
})
