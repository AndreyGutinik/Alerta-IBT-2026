import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { DEFAULT_AI_MODEL, readFirebaseConfig, REQUIRED_VARS, resolveAuthDomain } from './config'

function parseEnvFile(name: string): Record<string, string> {
  const text = readFileSync(resolve(import.meta.dirname, '../../', name), 'utf8')
  const out: Record<string, string> = {}
  for (const line of text.split(/\r?\n/)) {
    const m = /^([A-Z0-9_]+)=(.*)$/.exec(line.trim())
    if (m) out[m[1]] = m[2]
  }
  return out
}

describe('readFirebaseConfig', () => {
  it('ambiente vazio: lista as 5 obrigatórias', () => {
    expect(readFirebaseConfig({})).toEqual({ ok: false, missing: [...REQUIRED_VARS] })
    expect(REQUIRED_VARS).toHaveLength(5)
  })

  it('só uma faltando: lista só ela; valor em branco ou só espaços conta como faltando', () => {
    const env = parseEnvFile('.env.emulator')
    expect(readFirebaseConfig({ ...env, VITE_FIREBASE_APP_ID: undefined })).toEqual({
      ok: false,
      missing: ['VITE_FIREBASE_APP_ID'],
    })
    expect(readFirebaseConfig({ ...env, VITE_FIREBASE_API_KEY: '   ' })).toEqual({
      ok: false,
      missing: ['VITE_FIREBASE_API_KEY'],
    })
  })

  it('.env.emulator: configuração do emulador (projeto demo, IA padrão, sem reCAPTCHA)', () => {
    const result = readFirebaseConfig(parseEnvFile('.env.emulator'))
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.config.useEmulators).toBe(true)
    expect(result.config.firebase.projectId).toBe('demo-alertaibt')
    expect(result.config.firebase.projectId?.startsWith('demo-')).toBe(true)
    expect(result.config.aiModel).toBe(DEFAULT_AI_MODEL)
    expect(result.config.aiModel).toBe('gemini-3.5-flash-lite')
    expect(result.config.recaptchaSiteKey).toBeUndefined()
  })

  it('VITE_AI_MODEL e a chave do reCAPTCHA, quando definidos, são usados; VITE_USE_EMULATORS só vale "true"', () => {
    const env = { ...parseEnvFile('.env.emulator'), VITE_AI_MODEL: ' gemini-x ', VITE_RECAPTCHA_SITE_KEY: 'site-key' }
    const on = readFirebaseConfig(env)
    expect(on.ok && on.config.aiModel).toBe('gemini-x')
    expect(on.ok && on.config.recaptchaSiteKey).toBe('site-key')
    const off = readFirebaseConfig({ ...env, VITE_USE_EMULATORS: 'sim' })
    expect(off.ok && off.config.useEmulators).toBe(false)
  })

  it('.env.example tem todas as obrigatórias, vazias, e nunca traz segredo preenchido', () => {
    const example = parseEnvFile('.env.example')
    for (const name of REQUIRED_VARS) expect(example[name]).toBe('')
    expect(example.VITE_RECAPTCHA_SITE_KEY).toBe('')
    expect(example.VITE_AI_MODEL).toBe(DEFAULT_AI_MODEL)
    expect(example.VITE_USE_EMULATORS).toBeUndefined()
    expect(readFirebaseConfig(example).ok).toBe(false)
  })
})

describe('resolveAuthDomain', () => {
  it('em web.app e firebaseapp.com usa o próprio endereço; fora disso o configurado', () => {
    expect(resolveAuthDomain('alertaibt.firebaseapp.com', 'alertaibt.web.app')).toBe('alertaibt.web.app')
    expect(resolveAuthDomain('alertaibt.firebaseapp.com', 'alertaibt.firebaseapp.com')).toBe('alertaibt.firebaseapp.com')
    expect(resolveAuthDomain('alertaibt.firebaseapp.com', 'localhost')).toBe('alertaibt.firebaseapp.com')
    expect(resolveAuthDomain('x.firebaseapp.com', 'evil.web.app.example.com')).toBe('x.firebaseapp.com')
  })
})
