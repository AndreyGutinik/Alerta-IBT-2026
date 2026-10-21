import { useCallback, useEffect, useRef, useState } from 'react'

// Tipos mínimos da Web Speech API (o TypeScript não traz `SpeechRecognition`).
interface SpeechAlternative {
  transcript: string
}
interface SpeechResult {
  0: SpeechAlternative
  length: number
}
export interface SpeechRecognitionEventLike {
  results: ArrayLike<SpeechResult>
}
export interface SpeechRecognitionLike {
  lang: string
  continuous: boolean
  interimResults: boolean
  maxAlternatives: number
  onresult: ((e: SpeechRecognitionEventLike) => void) | null
  onerror: ((e: { error?: string }) => void) | null
  onend: (() => void) | null
  start(): void
  stop(): void
  abort(): void
}
export type SpeechRecognitionCtor = new () => SpeechRecognitionLike

/** O ditado do navegador (`SpeechRecognition` ou o `webkit` do Chrome/Android), ou `null` se não existe. */
export function getSpeechRecognition(win: Window = window): SpeechRecognitionCtor | null {
  const w = win as unknown as { SpeechRecognition?: SpeechRecognitionCtor; webkitSpeechRecognition?: SpeechRecognitionCtor }
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null
}

/** Ditado em pt-BR, uma frase por vez. `onError` recebe o código do navegador (ex.: `not-allowed`). */
export function useSpeech(
  onText: (t: string) => void,
  onError?: (code: string) => void,
): { supported: boolean; listening: boolean; start(): void } {
  const [Ctor] = useState(() => getSpeechRecognition())
  const [listening, setListening] = useState(false)
  const current = useRef<SpeechRecognitionLike | null>(null)
  const handlers = useRef({ onText, onError })
  handlers.current = { onText, onError }

  const start = useCallback(() => {
    if (!Ctor) return
    if (current.current) {
      // Segundo toque enquanto ouve: para.
      current.current.stop()
      return
    }
    const rec = new Ctor()
    rec.lang = 'pt-BR'
    rec.continuous = false
    rec.interimResults = false
    rec.maxAlternatives = 1
    rec.onresult = (e) => {
      const text = Array.from(e.results)
        .map((r) => r[0]?.transcript ?? '')
        .join(' ')
        .trim()
      if (text) handlers.current.onText(text)
    }
    rec.onerror = (e) => handlers.current.onError?.(e.error ?? 'error')
    rec.onend = () => {
      current.current = null
      setListening(false)
    }
    current.current = rec
    setListening(true)
    try {
      rec.start()
    } catch {
      current.current = null
      setListening(false)
      handlers.current.onError?.('start-failed')
    }
  }, [Ctor])

  useEffect(
    () => () => {
      const rec = current.current
      if (rec) {
        rec.onresult = null
        rec.onerror = null
        rec.onend = null
        rec.abort()
      }
    },
    [],
  )

  return { supported: Ctor !== null, listening, start }
}
