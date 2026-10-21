import type { FirebaseOptions } from 'firebase/app'

export interface AppConfig {
  firebase: FirebaseOptions
  /** Fala com os emuladores locais (Auth 9099, Database 9000) em vez da nuvem. */
  useEmulators: boolean
  /** Chave do site do reCAPTCHA v3 do App Check. Sem ela a IA fica desligada (só o parser local). */
  recaptchaSiteKey?: string
  aiModel: string
}

export const DEFAULT_AI_MODEL = 'gemini-3.5-flash-lite'

/** As variáveis que o site precisa para falar com um projeto do Firebase. */
export const REQUIRED_VARS = [
  'VITE_FIREBASE_API_KEY',
  'VITE_FIREBASE_AUTH_DOMAIN',
  'VITE_FIREBASE_PROJECT_ID',
  'VITE_FIREBASE_DATABASE_URL',
  'VITE_FIREBASE_APP_ID',
] as const

export type ConfigResult = { ok: true; config: AppConfig } | { ok: false; missing: string[] }

function clean(value: string | undefined): string {
  return (value ?? '').trim()
}

/** Lê a configuração do ambiente (`import.meta.env`). Faltando algo obrigatório, devolve o que falta. */
export function readFirebaseConfig(env: Record<string, string | boolean | undefined>): ConfigResult {
  const get = (name: string): string => {
    const value = env[name]
    return typeof value === 'string' ? clean(value) : ''
  }
  const missing = REQUIRED_VARS.filter((name) => get(name) === '')
  if (missing.length > 0) return { ok: false, missing }

  const config: AppConfig = {
    firebase: {
      apiKey: get('VITE_FIREBASE_API_KEY'),
      authDomain: get('VITE_FIREBASE_AUTH_DOMAIN'),
      projectId: get('VITE_FIREBASE_PROJECT_ID'),
      databaseURL: get('VITE_FIREBASE_DATABASE_URL'),
      appId: get('VITE_FIREBASE_APP_ID'),
    },
    useEmulators: get('VITE_USE_EMULATORS') === 'true',
    aiModel: get('VITE_AI_MODEL') || DEFAULT_AI_MODEL,
  }
  const site = get('VITE_RECAPTCHA_SITE_KEY')
  if (site) config.recaptchaSiteKey = site
  return { ok: true, config }
}

/**
 * Em `*.web.app` / `*.firebaseapp.com` o login passa pelo próprio endereço do site (evita o bloqueio de
 * cookies de terceiros nos navegadores); em qualquer outro endereço vale o `authDomain` configurado.
 */
export function resolveAuthDomain(configured: string, hostname: string): string {
  return hostname.endsWith('.web.app') || hostname.endsWith('.firebaseapp.com') ? hostname : configured
}
