import { act, fireEvent, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanRaw } from '../alerts/format'
import { liveState } from '../test/fakes'
import { renderActive, type RenderOptions } from '../test/helpers'
import { SendTab } from './SendTab'

let vibrate: ReturnType<typeof vi.fn>
beforeEach(() => {
  vibrate = vi.fn()
  Object.defineProperty(navigator, 'vibrate', { value: vibrate, configurable: true, writable: true })
})
afterEach(() => {
  vi.useRealTimers()
  Reflect.deleteProperty(navigator, 'vibrate')
  Reflect.deleteProperty(window, 'SpeechRecognition')
})

const GOOD = 'prata corolla bra2e19 vidro aberto'
const field = () => screen.getByRole('textbox', { name: 'Novo aviso' }) as HTMLTextAreaElement
const sendBtn = () => screen.getByRole('button', { name: 'Enviar' })
const urgentBtn = () => screen.getByRole('button', { name: 'Segure 1 s para urgente' })
const setText = (value: string) => fireEvent.change(field(), { target: { value } })
const advance = (ms: number) =>
  act(() => {
    vi.advanceTimersByTime(ms)
  })
const flush = () => act(async () => {})

function setup(opts: RenderOptions = {}) {
  return renderActive(<SendTab />, opts)
}

describe('SendTab: enviar', () => {
  it('Enviar manda o pedido com a prévia, limpa o campo e avisa', async () => {
    const { backend } = setup()
    setText(`  ${GOOD}  `)
    expect(sendBtn()).toBeEnabled()
    await userEvent.click(sendBtn())
    await flush()
    expect(backend.sent).toEqual([
      {
        uid: 'u1',
        name: 'Joana Silva',
        raw: cleanRaw(`  ${GOOD}  `),
        urgent: false,
        preview: { text: 'COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO', kind: 'vehicle', plate: 'BRA-2E19', by: 'local' },
      },
    ])
    expect(field().value).toBe('')
    expect(screen.getByText('Enviado')).toBeInTheDocument()
  })

  it('o aviso "Enviado" some depois de 1800 ms', async () => {
    vi.useFakeTimers()
    setup()
    setText(GOOD)
    await act(async () => {
      fireEvent.click(sendBtn())
    })
    expect(screen.getByText('Enviado')).toBeInTheDocument()
    advance(1800)
    expect(screen.queryByText('Enviado')).not.toBeInTheDocument()
  })

  it('Enviar e Urgente são só ícones, na mesma linha', () => {
    setup()
    expect(sendBtn()).toHaveTextContent('')
    expect(sendBtn().parentElement).toBe(urgentBtn().parentElement)
    expect(sendBtn().parentElement).toHaveClass('sendrow')
  })

  it('Review Focus 4: dois cliques seguidos em Enviar mandam um pedido só', async () => {
    const { backend } = setup()
    backend.sendImpl = () => new Promise(() => {})
    setText(GOOD)
    fireEvent.click(sendBtn())
    fireEvent.click(sendBtn())
    await userEvent.dblClick(sendBtn())
    expect(backend.sent).toHaveLength(1)
  })

  it('Review Focus 4: dois toques no mesmo instante (antes de a tela redesenhar) mandam um pedido só', () => {
    const { backend } = setup()
    backend.sendImpl = () => new Promise(() => {})
    setText(GOOD)
    act(() => {
      sendBtn().click()
      sendBtn().click()
    })
    expect(backend.sent).toHaveLength(1)
  })

  it('Review Focus 4: tocar em Enviar durante o segurar do Urgente manda um pedido só', async () => {
    vi.useFakeTimers()
    const { backend } = setup()
    setText(GOOD)
    fireEvent.pointerDown(urgentBtn())
    advance(500)
    await act(async () => {
      fireEvent.click(sendBtn())
    })
    advance(2000)
    await flush()
    expect(backend.sent).toHaveLength(1)
    expect(backend.sent[0].urgent).toBe(false)
  })

  it('Review Focus 4: o segurar termina enquanto o envio normal ainda está no ar: um pedido só', async () => {
    vi.useFakeTimers()
    const { backend } = setup()
    backend.sendImpl = () => new Promise(() => {})
    setText(GOOD)
    fireEvent.pointerDown(urgentBtn())
    advance(500)
    fireEvent.click(sendBtn())
    advance(2000)
    expect(backend.sent).toHaveLength(1)
  })

  it('Urgente segurado 1 s manda urgent true, vibra e avisa', async () => {
    vi.useFakeTimers()
    const { backend } = setup()
    setText(GOOD)
    fireEvent.pointerDown(urgentBtn())
    await act(async () => {
      vi.advanceTimersByTime(1000)
    })
    expect(vibrate).toHaveBeenCalledWith(60)
    expect(backend.sent).toHaveLength(1)
    expect(backend.sent[0].urgent).toBe(true)
    expect(screen.getByText('Enviado como URGENTE')).toBeInTheDocument()
    expect(field().value).toBe('')
  })

  it('toque rápido no Urgente não envia e ensina', () => {
    vi.useFakeTimers()
    const { backend } = setup()
    setText(GOOD)
    fireEvent.pointerDown(urgentBtn())
    advance(300)
    fireEvent.pointerUp(urgentBtn())
    expect(backend.sent).toHaveLength(0)
    expect(screen.getByText('Segure o ícone 1 s para urgente')).toBeInTheDocument()
  })

  it('prévia que não pode enviar: Enviar e Urgente desabilitados', () => {
    setup()
    setText('gol branco farol aceso')
    expect(sendBtn()).toBeDisabled()
    expect(urgentBtn()).toHaveAttribute('aria-disabled', 'true')
  })

  it('/live pausado: nada envia e mostra o motivo', () => {
    setup({ live: liveState({ paused: true }) })
    setText(GOOD)
    expect(sendBtn()).toBeDisabled()
    expect(urgentBtn()).toHaveAttribute('aria-disabled', 'true')
    expect(
      screen.getByText(`${String.fromCodePoint(0x23f8, 0xfe0f)} Pregação em andamento: o telão não está mostrando avisos.`),
    ).toBeInTheDocument()
  })

  it('membro que enviou há 5 s espera 15 s; admin não espera', () => {
    const first = setup({ record: { lastSentAt: 1_800_000_000_000 - 5000 } })
    setText(GOOD)
    expect(sendBtn()).toBeDisabled()
    expect(screen.getByText('Aguarde 15 s para mandar outro aviso.')).toBeInTheDocument()
    first.unmount()

    setup({ role: 'admin', record: { lastSentAt: 1_800_000_000_000 - 5000 } })
    setText(GOOD)
    expect(sendBtn()).toBeEnabled()
    expect(screen.queryByText(/Aguarde/)).not.toBeInTheDocument()
  })

  it('a contagem regressiva libera o Enviar sozinha', () => {
    vi.useFakeTimers()
    const { backend } = setup({ record: { lastSentAt: 1_800_000_000_000 - 5000 } })
    setText(GOOD)
    expect(sendBtn()).toBeDisabled()
    backend.now += 15_000
    advance(1000)
    expect(sendBtn()).toBeEnabled()
  })

  it('Enter no campo envia; Ctrl+Enter manda urgente; Enter sem prévia ok não faz nada', async () => {
    const { backend } = setup()
    await userEvent.click(field())
    await userEvent.keyboard('{Enter}')
    expect(backend.sent).toHaveLength(0)

    await userEvent.keyboard(GOOD)
    await userEvent.keyboard('{Enter}')
    expect(backend.sent).toHaveLength(1)
    expect(backend.sent[0].urgent).toBe(false)

    await userEvent.keyboard(GOOD)
    await userEvent.keyboard('{Control>}{Enter}{/Control}')
    expect(backend.sent).toHaveLength(2)
    expect(backend.sent[1].urgent).toBe(true)
  })

  it('Review Focus 1: sem conexão mostra erro na hora e não chama o backend nem apaga o texto', async () => {
    const { backend } = setup()
    backend.setConnected(false)
    setText(GOOD)
    await userEvent.click(sendBtn())
    expect(backend.sent).toHaveLength(0)
    expect(screen.getByText('Sem conexão. Confira a internet e tente de novo.')).toBeInTheDocument()
    expect(field().value).toBe(GOOD)
  })

  it('limite das regras (permission_denied): mensagem e o texto fica', async () => {
    const { backend } = setup()
    backend.sendImpl = () => Promise.reject({ code: 'PERMISSION_DENIED' })
    setText(GOOD)
    await userEvent.click(sendBtn())
    expect(await screen.findByText('Espere alguns segundos para mandar outro aviso.')).toBeInTheDocument()
    expect(field().value).toBe(GOOD)
    expect(sendBtn()).toBeEnabled()
  })

  it('o que a pessoa digitou durante o envio não é apagado', async () => {
    const { backend } = setup()
    let finish: (id: string) => void = () => {}
    backend.sendImpl = () => new Promise<string>((resolve) => (finish = resolve))
    setText(GOOD)
    fireEvent.click(sendBtn())
    setText('outro aviso novo')
    await act(async () => finish('r1'))
    expect(field().value).toBe('outro aviso novo')
  })

  it('com IA ainda corrigindo, Enviar fica desabilitado até a prévia chegar', async () => {
    vi.useFakeTimers()
    const corrector = {
      correct: () =>
        Promise.resolve({
          kind: 'vehicle' as const,
          modelo: 'Gol',
          cor: 'branco',
          placa: 'FFO4745',
          motivo: '',
          texto: '',
        }),
    }
    const { backend } = setup({ corrector })
    setText('gol branco ffo4745')
    expect(sendBtn()).toBeDisabled()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(700)
    })
    expect(sendBtn()).toBeEnabled()
    await act(async () => {
      fireEvent.click(sendBtn())
    })
    expect(backend.sent[0].preview?.by).toBe('gemini')
  })

  it('admin: texto livre vai como aviso livre, sem placa', async () => {
    const { backend } = setup({ role: 'admin' })
    setText('pais do pedro chegou')
    await userEvent.click(sendBtn())
    expect(backend.sent[0].preview).toEqual({ text: 'PAIS DO PEDRO CHEGOU', kind: 'free', by: 'local' })
  })

  it('PC offline: avisa, mas deixa enviar (o pedido espera o PC)', () => {
    setup({ live: liveState({ heartbeatAt: 1_800_000_000_000 - 120_000 }) })
    setText(GOOD)
    expect(screen.getByText('PC offline: o aviso só passa quando ele voltar.')).toBeInTheDocument()
    expect(sendBtn()).toBeEnabled()
  })

  it('ditado acrescenta ao texto com um espaço (e preenche o campo vazio)', async () => {
    class Rec {
      static last: Rec
      onresult: ((e: { results: unknown }) => void) | null = null
      onend: (() => void) | null = null
      onerror = null
      lang = ''
      continuous = false
      interimResults = false
      maxAlternatives = 1
      constructor() {
        Rec.last = this
      }
      start() {}
      stop() {}
      abort() {}
    }
    Object.defineProperty(window, 'SpeechRecognition', { value: Rec, configurable: true, writable: true })
    setup()
    const say = (t: string) =>
      act(() => {
        Rec.last.onresult?.({ results: [{ 0: { transcript: t }, length: 1 }] })
        Rec.last.onend?.()
      })
    await userEvent.click(screen.getByRole('button', { name: 'Ditar' }))
    expect(screen.getByText('Ouvindo…')).toBeInTheDocument()
    say('corolla preto qwe2f45')
    expect(field().value).toBe('corolla preto qwe2f45')
    await userEvent.click(screen.getByRole('button', { name: 'Ditar' }))
    say('farol aceso')
    expect(field().value).toBe('corolla preto qwe2f45 farol aceso')
  })

  it('sem ditado no navegador não há botão Ditar', () => {
    setup()
    expect(screen.queryByRole('button', { name: 'Ditar' })).not.toBeInTheDocument()
  })

  it('o balão (i) traz as dicas de teclado (e não há parágrafo visível com elas)', async () => {
    setup()
    expect(screen.queryByText(/Ctrl\+Enter/)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Ajuda' }))
    expect(screen.getByRole('note')).toHaveTextContent('Enter envia, Ctrl+Enter manda urgente, Esc limpa.')
  })
})
