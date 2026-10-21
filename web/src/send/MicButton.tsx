import { Mic } from 'lucide-react'
import { useSpeech } from './useSpeech'

/** O microfone pequeno e apagado dentro do campo. Some quando o navegador não tem ditado. */
export function MicButton({ onText, onMessage }: { onText: (t: string) => void; onMessage: (msg: string) => void }) {
  const speech = useSpeech(onText, (code) =>
    onMessage(
      code === 'not-allowed' || code === 'service-not-allowed'
        ? 'Permita o microfone nas configurações do navegador.'
        : code === 'no-speech'
          ? 'Não ouvi nada. Tente de novo.'
          : 'Não deu para ouvir. Tente de novo.',
    ),
  )
  if (!speech.supported) return null

  return (
    <button
      type="button"
      className={speech.listening ? 'mic rec' : 'mic'}
      aria-label="Ditar"
      aria-pressed={speech.listening}
      onClick={() => {
        if (!speech.listening) onMessage('Ouvindo…')
        speech.start()
      }}
    >
      <Mic aria-hidden="true" />
    </button>
  )
}
