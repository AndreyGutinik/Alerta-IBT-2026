import { AI_SCHEMA, SYSTEM_PROMPT, parseAiJson, thinkingConfigFor } from './prompt'
import { AI_TIMEOUT_MS } from './correct'
import type { FirebaseApp } from 'firebase/app'
import { ReCaptchaEnterpriseProvider, initializeAppCheck } from 'firebase/app-check'
import { GoogleAIBackend, Schema, getAI, getGenerativeModel } from 'firebase/ai'
import type { AiAlert, AiCorrector } from './types'

// O adaptador real da IA: Firebase AI Logic (Gemini Developer API) protegido por App Check. Só entra no
// pacote quando há chave do App Check (quem usa carrega este arquivo com `import()`); os testes e as telas
// falam com a interface `AiCorrector`. Não há como testar contra o serviço de verdade sem projeto no
// Firebase: o teste confere só a configuração que este arquivo manda para o SDK.

export interface FirebaseAiOptions {
  /** O modelo (padrão do PC: `DEFAULT_MODEL`). */
  model: string
  /** A chave do site do reCAPTCHA v3 que o App Check usa. */
  recaptchaSiteKey: string
}

// O schema do PC (AI_SCHEMA) no formato do SDK: todos os campos obrigatórios.
function responseSchema() {
  return Schema.object({
    properties: {
      kind: Schema.enumString({ enum: [...AI_SCHEMA.properties.kind.enum] }),
      modelo: Schema.string(),
      cor: Schema.string(),
      placa: Schema.string(),
      motivo: Schema.string(),
      texto: Schema.string(),
    },
  })
}

export function createFirebaseAiCorrector(app: FirebaseApp, opts: FirebaseAiOptions): AiCorrector {
  if (import.meta.env.DEV) {
    // Em desenvolvimento o App Check usa um token de depuração (aparece no console do navegador).
    ;(self as unknown as { FIREBASE_APPCHECK_DEBUG_TOKEN?: boolean }).FIREBASE_APPCHECK_DEBUG_TOKEN = true
  }
  initializeAppCheck(app, {
    provider: new ReCaptchaEnterpriseProvider(opts.recaptchaSiteKey),
    isTokenAutoRefreshEnabled: true,
  })

  const ai = getAI(app, { backend: new GoogleAIBackend(), useLimitedUseAppCheckTokens: true })
  const thinkingConfig = thinkingConfigFor(opts.model)
  const model = getGenerativeModel(
    ai,
    {
      model: opts.model,
      systemInstruction: SYSTEM_PROMPT,
      generationConfig: {
        responseMimeType: 'application/json',
        responseSchema: responseSchema(),
        temperature: 0,
        ...(thinkingConfig ? { thinkingConfig } : {}),
      },
    },
    { timeout: AI_TIMEOUT_MS },
  )

  return {
    async correct(raw: string, signal: AbortSignal): Promise<AiAlert | null> {
      try {
        const result = await model.generateContent(raw, { signal })
        return parseAiJson(result.response.text())
      } catch {
        // Sem rede, cota estourada, tempo esgotado, resposta bloqueada ou fora do formato: não deu.
        return null
      }
    },
  }
}
