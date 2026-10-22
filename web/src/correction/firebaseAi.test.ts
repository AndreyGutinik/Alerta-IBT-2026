import type { FirebaseApp } from 'firebase/app'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AI_TIMEOUT_MS } from './correct'
import { SYSTEM_PROMPT } from './prompt'

// O adaptador real não pode ser exercitado sem um projeto no Firebase; aqui o SDK é trocado por um falso
// e o teste confere o que o adaptador manda para ele e como trata a resposta.
const sdk = vi.hoisted(() => ({
  generateContent: vi.fn(),
  getGenerativeModel: vi.fn(),
  getAI: vi.fn(() => ({ ai: true })),
  initializeAppCheck: vi.fn(),
  provider: vi.fn(),
}))

vi.mock('firebase/ai', () => ({
  GoogleAIBackend: class {},
  Schema: {
    object: (spec: unknown) => ({ object: spec }),
    string: () => 'string',
    enumString: (spec: unknown) => ({ enum: spec }),
  },
  getAI: sdk.getAI,
  getGenerativeModel: (...args: unknown[]) => {
    sdk.getGenerativeModel(...args)
    return { generateContent: sdk.generateContent }
  },
}))
vi.mock('firebase/app-check', () => ({
  ReCaptchaEnterpriseProvider: class {
    constructor(key: string) {
      sdk.provider(key)
    }
  },
  initializeAppCheck: sdk.initializeAppCheck,
}))

const app = {} as FirebaseApp

beforeEach(() => {
  vi.clearAllMocks()
})

async function create(model = 'gemini-3.5-flash-lite') {
  const { createFirebaseAiCorrector } = await import('./firebaseAi')
  return createFirebaseAiCorrector(app, { model, recaptchaSiteKey: 'site-key' })
}

describe('createFirebaseAiCorrector', () => {
  it('liga o App Check com a chave do reCAPTCHA v3 e renovação automática', async () => {
    await create()

    expect(sdk.provider).toHaveBeenCalledWith('site-key')
    expect(sdk.initializeAppCheck).toHaveBeenCalledWith(app, expect.objectContaining({ isTokenAutoRefreshEnabled: true }))
    expect(sdk.getAI).toHaveBeenCalledWith(app, expect.objectContaining({ useLimitedUseAppCheckTokens: true }))
  })

  it('pede o mesmo que o PC: instrução, JSON com schema, temperatura 0, tempo máximo e pouco pensamento', async () => {
    await create()

    const [, params, requestOptions] = sdk.getGenerativeModel.mock.calls[0]
    expect(params.model).toBe('gemini-3.5-flash-lite')
    expect(params.systemInstruction).toBe(SYSTEM_PROMPT)
    expect(params.generationConfig).toMatchObject({
      responseMimeType: 'application/json',
      temperature: 0,
      thinkingConfig: { thinkingLevel: 'MINIMAL' },
    })
    expect(Object.keys(params.generationConfig.responseSchema.object.properties)).toEqual([
      'kind',
      'modelo',
      'cor',
      'placa',
      'motivo',
      'texto',
    ])
    expect(requestOptions).toEqual({ timeout: AI_TIMEOUT_MS })
  })

  it('modelo desconhecido fica sem configuração de pensamento', async () => {
    await create('outro-modelo')

    expect(sdk.getGenerativeModel.mock.calls[0][1].generationConfig).not.toHaveProperty('thinkingConfig')
  })

  it('devolve a resposta lida, mandando o texto e o signal', async () => {
    sdk.generateContent.mockResolvedValue({
      response: { text: () => '{"kind":"vehicle","modelo":"Gol","cor":"branco","placa":"FFO4745","motivo":"","texto":"Gol branco FFO4745"}' },
    })
    const corrector = await create()
    const signal = new AbortController().signal

    const answer = await corrector.correct('gol branco ffo4745', signal)

    expect(answer).toMatchObject({ kind: 'vehicle', modelo: 'Gol', placa: 'FFO4745' })
    expect(sdk.generateContent).toHaveBeenCalledWith('gol branco ffo4745', { signal })
  })

  it('erro do SDK, resposta bloqueada ou JSON ruim: null (quem chama cai no local)', async () => {
    const corrector = await create()
    const signal = new AbortController().signal

    sdk.generateContent.mockRejectedValueOnce(new Error('sem rede'))
    expect(await corrector.correct('x', signal)).toBeNull()

    sdk.generateContent.mockResolvedValueOnce({
      response: {
        text: () => {
          throw new Error('bloqueado')
        },
      },
    })
    expect(await corrector.correct('x', signal)).toBeNull()

    sdk.generateContent.mockResolvedValueOnce({ response: { text: () => 'não é json' } })
    expect(await corrector.correct('x', signal)).toBeNull()
  })
})
