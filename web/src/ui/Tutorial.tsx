import { ArrowDown, ArrowRight, Check, Clock, MonitorPlay, Siren } from 'lucide-react'
import { type ReactNode, useState } from 'react'

export const TUTORIAL_KEY = 'alertaibt.tutorial.v1'

/** O tutorial já foi concluído neste aparelho? (localStorage pode lançar erro ou estar vazio.) */
export function readTutorialDone(): boolean {
  try {
    return localStorage.getItem(TUTORIAL_KEY) === '1'
  } catch {
    return false
  }
}

function writeTutorialDone(): void {
  try {
    localStorage.setItem(TUTORIAL_KEY, '1')
  } catch {
    // Sem armazenamento: o tutorial volta no próximo acesso, só isso.
  }
}

interface Step {
  title: string
  text: string
  art: ReactNode
}

// Os 3 passos e os textos do mockup site-v5.
const STEPS: Step[] = [
  {
    title: 'Como mandar um aviso',
    text: 'Escreva do seu jeito, numa linha. O texto é corrigido e você vê como vai ficar no telão.',
    art: (
      <>
        <div className="ex1">corolla prata abc1d23 vidro abrto</div>
        <ArrowDown aria-hidden="true" />
        <div className="ex2">COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO</div>
      </>
    ),
  },
  {
    title: 'O que não pode faltar',
    text: 'Modelo, cor e placa (antiga ou Mercosul). O motivo é opcional. Se faltar algo, avisamos antes de enviar.',
    art: (
      <>
        <div className="ex2">MODELO · COR · PLACA</div>
        <div className="ex1">+ motivo (opcional)</div>
      </>
    ),
  },
  {
    title: 'Acompanhe e urgente',
    text: 'Em "Seus avisos" você vê o andamento. Para urgente, segure o ícone amarelo por 1 segundo.',
    art: (
      <>
        <div className="ex2 flow">
          <Clock aria-hidden="true" /> NA FILA <ArrowRight aria-hidden="true" /> <MonitorPlay aria-hidden="true" />{' '}
          PASSANDO <ArrowRight aria-hidden="true" /> <Check aria-hidden="true" /> EXIBIDO
        </div>
        <div className="ex1 flow">
          segure <Siren aria-hidden="true" /> 1 s = urgente
        </div>
      </>
    ),
  },
]

export function Tutorial({ onDone }: { onDone: () => void }) {
  const [step, setStep] = useState(0)
  const last = step === STEPS.length - 1
  const s = STEPS[step]

  function finish() {
    writeTutorialDone()
    onDone()
  }

  return (
    <main className="tut">
      <div className="ill">{s.art}</div>
      <h2>{s.title}</h2>
      <p>{s.text}</p>
      <div className="dots" aria-hidden="true">
        {STEPS.map((_, i) => (
          <i key={i} className={i === step ? 'on' : ''} />
        ))}
      </div>
      <div className="btns">
        <button type="button" className="skip" onClick={finish}>
          Pular
        </button>
        <button type="button" className="next" onClick={last ? finish : () => setStep(step + 1)}>
          {last ? 'Começar' : 'Próximo'}
          <ArrowRight aria-hidden="true" />
        </button>
      </div>
    </main>
  )
}
