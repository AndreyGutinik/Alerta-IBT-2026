import { Send } from 'lucide-react'
import { UrgentButton } from './UrgentButton'

/** Enviar (ícone grande, verde) e Urgente (ícone amarelo, segurar 1 s) na mesma linha. */
export function SendRow({
  canSend,
  onSend,
  onUrgent,
  onUrgentShortTap,
}: {
  canSend: boolean
  onSend: () => void
  onUrgent: () => void
  onUrgentShortTap: () => void
}) {
  return (
    <div className="sendrow">
      <button type="button" className="send" aria-label="Enviar" title="Enviar" disabled={!canSend} onClick={onSend}>
        <Send aria-hidden="true" />
      </button>
      <UrgentButton enabled={canSend} onUrgent={onUrgent} onShortTap={onUrgentShortTap} />
    </div>
  )
}
