import { useId, useState } from 'react'
import { fieldValue, formatFree } from '../alerts/format'
import { MEMBER_MAX_LENGTH } from '../alerts/rules'
import { useActiveSession } from '../session/SessionProvider'
import { useCorrector } from '../state/AppData'
import { InfoPopover } from './InfoPopover'
import { PreviewBox } from './PreviewBox'
import { usePreview } from './usePreview'

const HELP_MEMBER = [
  'Precisa de modelo, cor e placa.',
  'Ex.: corolla prata abc1d23 vidro aberto',
  'Segure o ícone amarelo 1 s para urgente.',
]
const HELP_ADMIN = ['Admin pode mandar qualquer texto.', 'Sem placa: vai como aviso livre, corrigido.']

/** O contador aparece perto do limite (80%) e só para o membro, que tem limite. */
const COUNTER_FROM = Math.floor(MEMBER_MAX_LENGTH * 0.8)

export function SendTab() {
  const { role } = useActiveSession()
  const corrector = useCorrector()
  const [raw, setRaw] = useState('')
  const preview = usePreview(raw, role, corrector)
  const inputId = useId()

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
              if (e.key === 'Enter') e.preventDefault()
              else if (e.key === 'Escape') setRaw('')
            }}
            placeholder={role === 'admin' ? 'Qualquer aviso…' : 'Ex.: corolla prata abc1d23 vidro aberto'}
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
          />
        </div>
      </div>
      <div className="cd">
        <PreviewBox status={preview.status} result={preview.result} role={role} />
      </div>
    </>
  )
}
