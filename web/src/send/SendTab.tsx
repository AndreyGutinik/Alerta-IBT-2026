import { useCallback, useId, useRef, useState } from 'react'
import { cleanRaw, fieldValue, formatFree } from '../alerts/format'
import { MEMBER_MAX_LENGTH } from '../alerts/rules'
import { isPcOnline, rateLimitLeft } from '../data/live'
import type { SendInput } from '../data/types'
import { useActiveSession } from '../session/SessionProvider'
import { useBackend, useConnected, useCorrector, useLive, useServerNow } from '../state/AppData'
import { Toast } from '../ui/Toast'
import { useToast } from '../ui/useToast'
import { InfoPopover } from './InfoPopover'
import { MicButton } from './MicButton'
import { MyAlerts } from './MyAlerts'
import { PreviewBox } from './PreviewBox'
import { sendAlert } from './sendFlow'
import { SendRow } from './SendRow'
import { usePreview } from './usePreview'

const KEYBOARD_HINT = 'No computador: Enter envia, Ctrl+Enter manda urgente, Esc limpa.'
const HELP_MEMBER = [
  'Precisa de modelo, cor e placa.',
  'Ex.: corolla prata abc1d23 vidro aberto',
  'Segure o ícone amarelo 1 s para urgente.',
  KEYBOARD_HINT,
]
const HELP_ADMIN = [
  'Admin pode mandar qualquer texto.',
  'Sem placa: vai como aviso livre, corrigido.',
  'Segure o ícone amarelo 1 s para urgente.',
  KEYBOARD_HINT,
]

/** ⏸ + seletor de emoji (montado por código para o arquivo não depender de como o editor grava emoji). */
const PAUSE_EMOJI = String.fromCodePoint(0x23f8, 0xfe0f)
export const MSG_PAUSED = `${PAUSE_EMOJI} Pregação em andamento: o telão não está mostrando avisos.`
export const MSG_PC_OFFLINE = 'PC offline: o aviso só passa quando ele voltar.'
export const MSG_URGENT_HINT = 'Segure o ícone 1 s para urgente'

/** O contador aparece perto do limite (80%) e só para o membro, que tem limite. */
const COUNTER_FROM = Math.floor(MEMBER_MAX_LENGTH * 0.8)

export function SendTab() {
  const { user, record, role } = useActiveSession()
  const backend = useBackend()
  const corrector = useCorrector()
  const live = useLive()
  const connected = useConnected()
  const serverNow = useServerNow()
  const { message: toastMessage, show } = useToast()
  const [raw, setRaw] = useState('')
  const [sending, setSending] = useState(false)
  const inputId = useId()
  const preview = usePreview(raw, role, corrector)

  // O PC manda: pausa do telão e limite por pessoa bloqueiam; PC offline só avisa.
  const rateLeft = rateLimitLeft(record, role, serverNow)
  const blocked = live?.paused ? MSG_PAUSED : rateLeft > 0 ? `Aguarde ${rateLeft} s para mandar outro aviso.` : ''
  const pcNote = !blocked && connected && !isPcOnline(live, serverNow) ? MSG_PC_OFFLINE : ''
  const canSend = preview.result?.canSend === true && blocked === '' && !sending

  // Sempre a versão mais nova: o segurar do urgente termina 1 s depois de começar.
  const latest = useRef({ raw, canSend, result: preview.result })
  latest.current = { raw, canSend, result: preview.result }
  const sendingRef = useRef(false)

  const submit = useCallback(
    async (urgent: boolean) => {
      const { raw: rawNow, canSend: ok, result } = latest.current
      // Toque duplo, ou um toque durante o segurar do urgente: um pedido só.
      if (sendingRef.current || !ok || !result || result.kind === 'reject') return
      sendingRef.current = true
      setSending(true)
      try {
        const input: SendInput = {
          uid: user.uid,
          name: record.name || user.name,
          raw: cleanRaw(rawNow),
          urgent,
          preview: {
            text: result.text,
            kind: result.kind,
            ...(result.plate ? { plate: result.plate } : {}),
            by: result.by,
          },
        }
        const outcome = await sendAlert(backend, input, { connected })
        if (outcome.ok) {
          // Se a pessoa já digitou outra coisa enquanto enviava, não apaga.
          setRaw((current) => (current === rawNow ? '' : current))
          show(urgent ? 'Enviado como URGENTE' : 'Enviado')
        } else {
          show(outcome.message, 4000)
        }
      } finally {
        sendingRef.current = false
        setSending(false)
      }
    },
    [backend, connected, record.name, show, user.name, user.uid],
  )

  const appendDictation = useCallback((text: string) => {
    setRaw((current) => fieldValue(current.trim() ? `${current.trimEnd()} ${text}` : text))
    show('')
  }, [show])

  const length = preview.result?.length ?? formatFree(raw).length
  const showCounter = role === 'member' && length >= COUNTER_FROM

  return (
    <>
      <div>
        <div className="lb">
          <label htmlFor={inputId}>Novo aviso</label>
          <span className="lb-r">
            {showCounter && (
              <span className={length > MEMBER_MAX_LENGTH ? 'count over' : 'count'}>
                {length}/{MEMBER_MAX_LENGTH}
              </span>
            )}
            <InfoPopover lines={role === 'admin' ? HELP_ADMIN : HELP_MEMBER} />
          </span>
        </div>
        <div className="inrow">
          <textarea
            id={inputId}
            rows={3}
            value={raw}
            onChange={(e) => setRaw(fieldValue(e.target.value))}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault()
                if (!e.nativeEvent.isComposing) void submit(e.ctrlKey || e.metaKey)
              } else if (e.key === 'Escape') {
                setRaw('')
              }
            }}
            enterKeyHint="send"
            placeholder={role === 'admin' ? 'Qualquer aviso…' : 'Ex.: corolla prata abc1d23 vidro aberto'}
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
          />
          <MicButton onText={appendDictation} onMessage={(msg) => show(msg)} />
        </div>
      </div>
      <div className="cd">
        <PreviewBox status={preview.status} result={preview.result} role={role} />
      </div>
      <SendRow
        canSend={canSend}
        onSend={() => void submit(false)}
        onUrgent={() => void submit(true)}
        onUrgentShortTap={() => show(MSG_URGENT_HINT)}
      />
      <Toast message={toastMessage || blocked || pcNote} live={toastMessage !== ''} />
      <MyAlerts uid={user.uid} />
    </>
  )
}
