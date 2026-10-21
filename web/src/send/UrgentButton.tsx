import { Siren } from 'lucide-react'
import { useHold } from './useHold'

export const URGENT_HOLD_MS = 1000

/** Vibração de confirmação do urgente. */
const VIBRATE_MS = 60

/**
 * O ícone amarelo de urgente: só vale segurando 1 s (anel de progresso à vista). Toque rápido não envia:
 * avisa como fazer. Desabilitado fica com `aria-disabled` (continua focável para o leitor de tela).
 */
export function UrgentButton({
  enabled,
  onUrgent,
  onShortTap,
}: {
  enabled: boolean
  onUrgent: () => void
  onShortTap: () => void
}) {
  const { holding, bind } = useHold(
    () => {
      try {
        navigator.vibrate?.(VIBRATE_MS)
      } catch {
        // Sem vibração neste aparelho.
      }
      onUrgent()
    },
    { ms: URGENT_HOLD_MS, enabled, onShortTap },
  )

  return (
    <button
      type="button"
      className={`urg${holding ? ' holding' : ''}${enabled ? '' : ' dis'}`}
      aria-label="Segure 1 s para urgente"
      aria-disabled={!enabled}
      {...bind}
    >
      <svg className="ring" viewBox="0 0 36 36" aria-hidden="true">
        <circle className="ring-track" cx="18" cy="18" r="16" />
        <circle className="ring-bar" cx="18" cy="18" r="16" />
      </svg>
      <Siren aria-hidden="true" />
    </button>
  )
}
