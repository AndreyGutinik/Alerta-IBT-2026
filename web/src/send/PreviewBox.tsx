import { CircleAlert, CopyX, LoaderCircle, Megaphone, WandSparkles } from 'lucide-react'
import type { ReactNode } from 'react'
import { MEMBER_MAX_LENGTH } from '../alerts/rules'
import type { Role } from '../alerts/types'
import type { Duplicate } from '../data/live'
import type { PreviewResult } from './evaluate'
import type { PreviewStatus } from './usePreview'

function duplicateLine(d: Duplicate): string {
  switch (d.where) {
    case 'queue':
      return `ESSE CARRO JÁ ESTÁ NA FILA (${d.label})`
    case 'now':
      return `ESSE CARRO ESTÁ PASSANDO NO TELÃO (${d.label})`
    case 'recent':
      return `ESSE CARRO JÁ PASSOU HÁ ${d.minutesAgo ?? 1} MIN (${d.label})`
  }
}

interface View {
  cls: string
  icon: ReactNode
  head: ReactNode
  body: ReactNode
}

function viewOf(status: PreviewStatus, result: PreviewResult | null, role: Role): View {
  if (status === 'empty') {
    return { cls: 'pre', icon: <WandSparkles />, head: 'Vai aparecer assim:', body: '…' }
  }
  if (status === 'busy' || !result) {
    return { cls: 'pre busy', icon: <LoaderCircle />, head: null, body: 'Corrigindo com IA…' }
  }
  if (result.refused) {
    return { cls: 'pre err', icon: <CircleAlert />, head: 'Não vai para o telão:', body: 'TEXTO NÃO ACEITO' }
  }
  if (result.tooLong) {
    return {
      cls: 'pre err',
      icon: <CircleAlert />,
      head: 'Não vai para o telão:',
      body: `TEXTO GRANDE DEMAIS (MÁXIMO ${MEMBER_MAX_LENGTH} LETRAS)`,
    }
  }
  if (result.duplicate) {
    const line = duplicateLine(result.duplicate)
    return role === 'admin'
      ? {
          cls: 'pre dup',
          icon: <CopyX />,
          head: 'Atenção, pode mandar mesmo assim:',
          body: line,
        }
      : { cls: 'pre dup', icon: <CopyX />, head: 'Não precisa mandar:', body: line }
  }
  if (result.kind === 'reject') {
    const body = result.missing.length > 0 ? `FALTA ${result.missing.join(', ').toUpperCase()}` : 'SEM TEXTO PARA MOSTRAR'
    return { cls: 'pre err', icon: <CircleAlert />, head: 'Não vai para o telão:', body }
  }
  if (result.kind === 'free') {
    return {
      cls: 'pre free',
      icon: <Megaphone />,
      head: (
        <>
          Vai aparecer assim <span className="tag">aviso livre</span>
        </>
      ),
      body: result.text,
    }
  }
  return { cls: 'pre', icon: <WandSparkles />, head: 'Vai aparecer assim:', body: result.text }
}

/** A prévia ao vivo, na caixa do mockup. `aria-live` para leitor de tela acompanhar. */
export function PreviewBox({
  status,
  result,
  role,
}: {
  status: PreviewStatus
  result: PreviewResult | null
  role: Role
}) {
  const view = viewOf(status, result, role)
  return (
    <div className={view.cls} aria-live="polite">
      <span className="pre-icon" aria-hidden="true">
        {view.icon}
      </span>
      <span>
        {view.head && <span className="k">{view.head}</span>}
        {view.body}
      </span>
    </div>
  )
}
