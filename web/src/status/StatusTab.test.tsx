import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { formatHm } from '../data/time'
import type { LiveState } from '../data/types'
import { FakeAuth, FakeBackend, liveState, TEST_USER, userRecord } from '../test/fakes'
import { renderActive } from '../test/helpers'
import { AppDataProvider } from '../state/AppData'
import { Root } from '../ui/Root'
import { TUTORIAL_KEY } from '../ui/Tutorial'
import { MiniLed } from './MiniLed'
import { StatusTab } from './StatusTab'

const T = 1_800_000_000_000
const NOW_TEXT = 'CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO'

function fullLive(over: Partial<LiveState> = {}): LiveState {
  return liveState(
    {
      now: { id: 'n1', text: NOW_TEXT, source: 'telegram', startedAt: T - 5000 },
      queue: [
        { id: 'q1', text: 'GOL BRANCO | PLACA: FFO-4745 | ESTACIONADO NA CONTRAMÃO', source: 'site' },
        { id: 'q2', text: 'COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO', source: 'pc', urgent: true },
      ],
      recent: [
        { id: 'r1', text: 'FOX PRATA | PLACA: ETC-6785 | LOCAL PROIBIDO', state: 'shown', at: T - 600_000 },
        { id: 'r2', text: 'UNO AZUL | PLACA: AAA-1111 | X', state: 'rejected', at: T - 900_000 },
      ],
      ...over,
    },
    T,
  )
}

beforeEach(() => localStorage.setItem(TUTORIAL_KEY, '1'))
afterEach(() => {
  localStorage.clear()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('StatusTab', () => {
  it('mostra o aviso no telão com selo, mini-telão, pílulas, fila e recentes', () => {
    renderActive(<StatusTab />, { live: fullLive() })
    expect(screen.getByText('No telão agora')).toBeInTheDocument()
    expect(screen.getByText('Telegram')).toBeInTheDocument()
    expect(screen.getByText(NOW_TEXT, { selector: '.msg' })).toBeInTheDocument()
    expect(screen.getByTestId('mini-led').querySelector('.tick')).toHaveTextContent(NOW_TEXT)
    expect(screen.getByText('LED ligado')).toBeInTheDocument()
    expect(screen.getByText('Barra ativa')).toBeInTheDocument()

    expect(screen.getByRole('heading', { name: 'Fila (2)' })).toBeInTheDocument()
    const items = screen.getAllByRole('listitem')
    expect(within(items[0]).getByText('Site')).toBeInTheDocument()
    expect(within(items[0]).getByText('1º')).toBeInTheDocument()
    expect(within(items[1]).getByText('PC')).toBeInTheDocument()
    expect(within(items[1]).getByText('2º')).toBeInTheDocument()
    expect(within(items[1]).getByRole('img', { name: 'Urgente' })).toBeInTheDocument()

    expect(screen.getByRole('heading', { name: 'Recentes' })).toBeInTheDocument()
    expect(screen.getByText(formatHm(T - 600_000))).toBeInTheDocument()
    expect(screen.getByText('recusado')).toBeInTheDocument()
  })

  it('LED desligado e barra pausada têm a própria pílula e a barra some do mini-telão', () => {
    const { backend, unmount } = renderActive(<StatusTab />, { live: fullLive({ led: false }) })
    expect(screen.getByText('LED desligado')).toBeInTheDocument()
    expect(screen.getByTestId('mini-led').querySelector('.bar')).toBeNull()
    unmount()
    expect(backend.listenerCount).toBe(0)

    renderActive(<StatusTab />, { live: fullLive({ paused: true }) })
    expect(screen.getByText('Barra pausada', { selector: '.pill' })).toBeInTheDocument()
    expect(screen.getByTestId('mini-led').querySelector('.bar')).toBeNull()
  })

  it('Review Focus 2: heartbeat de 91 s = PC offline e o aviso velho some do card', () => {
    renderActive(<StatusTab />, { live: fullLive({ heartbeatAt: T - 91_000 }) })
    expect(screen.queryByText(NOW_TEXT)).not.toBeInTheDocument()
    expect(screen.getByText('PC offline. Sem informação do telão agora.')).toBeInTheDocument()
    expect(screen.getByTestId('mini-led').querySelector('.bar')).toBeNull()
    expect(screen.queryByText('LED ligado')).not.toBeInTheDocument()
    expect(screen.queryByText('Telegram')).not.toBeInTheDocument()
  })

  it('90 s exatos ainda é online', () => {
    renderActive(<StatusTab />, { live: fullLive({ heartbeatAt: T - 90_000 }) })
    expect(screen.getByText(NOW_TEXT, { selector: '.msg' })).toBeInTheDocument()
  })

  it('só o tempo passando vira PC offline (sem novo /live)', () => {
    vi.useFakeTimers()
    const { backend } = renderActive(<StatusTab />, { live: fullLive({ heartbeatAt: T - 80_000 }) })
    expect(screen.getByText(NOW_TEXT, { selector: '.msg' })).toBeInTheDocument()
    backend.now = T + 12_000
    act(() => {
      vi.advanceTimersByTime(1000)
    })
    expect(screen.queryByText(NOW_TEXT)).not.toBeInTheDocument()
  })

  it('sem /live (nunca chegou) = PC offline, com fila e recentes vazios', () => {
    renderActive(<StatusTab />, { live: null })
    expect(screen.getByText('PC offline. Sem informação do telão agora.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Fila (0)' })).toBeInTheDocument()
    expect(screen.getByText('Fila vazia.')).toBeInTheDocument()
    expect(screen.getByText('Nada por enquanto.')).toBeInTheDocument()
  })

  it('PC online sem aviso passando', () => {
    renderActive(<StatusTab />, { live: liveState({}, T) })
    expect(screen.getByText('Nenhum aviso no telão agora.')).toBeInTheDocument()
    expect(screen.getByTestId('mini-led').querySelector('.bar')).toBeNull()
  })

  it('celular sem conexão: avisa que pode estar desatualizado e esmaece a lista', () => {
    const { backend } = renderActive(<StatusTab />, { live: fullLive() })
    backend.setConnected(false)
    expect(screen.getByText('Sem conexão: o que aparece aqui pode estar desatualizado.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Fila (2)' }).closest('section')).toHaveClass('stale')
  })

  it('admin cancela só o item do site; chama backend.cancel(id)', async () => {
    const { backend } = renderActive(<StatusTab />, { role: 'admin', live: fullLive() })
    const buttons = screen.getAllByRole('button', { name: 'Cancelar aviso' })
    expect(buttons).toHaveLength(1)
    expect(buttons[0].closest('li')).toHaveTextContent('GOL BRANCO')
    await userEvent.click(buttons[0])
    expect(backend.cancelled).toEqual(['q1'])
    expect(buttons[0]).toBeDisabled()
  })

  it('erro ao cancelar mostra aviso e libera o botão', async () => {
    const backend = new FakeBackend()
    backend.cancelImpl = () => Promise.reject(new Error('x'))
    renderActive(<StatusTab />, { role: 'admin', live: fullLive(), backend })
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar aviso' }))
    expect(await screen.findByText('Não deu para cancelar. Tente de novo.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancelar aviso' })).toBeEnabled()
  })

  it('membro não vê Cancelar aviso', () => {
    renderActive(<StatusTab />, { role: 'member', live: fullLive() })
    expect(screen.queryByRole('button', { name: 'Cancelar aviso' })).not.toBeInTheDocument()
  })

  it('recentes: do mais novo para o mais velho, no máximo 10', () => {
    const recent = Array.from({ length: 12 }, (_, i) => ({
      id: `r${i}`,
      text: `AVISO ${i}`,
      state: 'shown' as const,
      at: T - i * 1000,
    }))
    renderActive(<StatusTab />, { live: fullLive({ recent: [...recent].reverse() }) })
    const list = screen.getByRole('heading', { name: 'Recentes' }).closest('section')!
    const texts = within(list).getAllByRole('listitem').map((li) => li.querySelector('.t')?.textContent)
    expect(texts).toHaveLength(10)
    expect(texts[0]).toBe('AVISO 0')
    expect(texts[9]).toBe('AVISO 9')
  })
})

const headerPill = () => document.querySelector('.ab .pill')

describe('Shell na aba Status', () => {
  it('título Agora e pílula PC online no cabeçalho; offline troca a pílula', async () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord())
    backend.emitLive(fullLive())
    render(<Root auth={new FakeAuth(TEST_USER)} backend={backend} corrector={Promise.resolve(null)} />)
    await userEvent.click(screen.getByRole('tab', { name: 'Status' }))
    expect(screen.getByRole('heading', { name: 'Agora' })).toBeInTheDocument()
    expect(headerPill()).toHaveTextContent('PC online')
    expect(await screen.findByText('No telão agora')).toBeInTheDocument()

    backend.emitLive(fullLive({ heartbeatAt: T - 200_000 }))
    expect(headerPill()).toHaveTextContent('PC offline')
  })
})

describe('MiniLed', () => {
  it('anda pelo tempo dos quadros a partir do startedAt (hora do servidor)', () => {
    const frames: FrameRequestCallback[] = []
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => frames.push(cb))
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.spyOn(performance, 'now').mockReturnValue(1000)
    const bar = { position: 'top' as const, height: 56, speed: 90 }

    renderActive(<MiniLed text="OLA" bar={bar} startedAt={T - 10_000} />, { live: null })
    const tick = screen.getByTestId('mini-led').querySelector<HTMLElement>('.tick')!
    // 10 s passados a 90 px/s: 2560 - 900 = 1660 px = 64.84375 cqw
    expect(tick.style.transform).toBe('translate(64.84375cqw, -50%)')
    act(() => frames[frames.length - 1](2000)) // +1 s = +90 px
    expect(tick.style.transform).toBe('translate(61.328125cqw, -50%)')
  })

  it('tamanho da barra vem de /live/bar (cqw) e sem texto não há barra', () => {
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    const bar = { position: 'bottom' as const, height: 76.8, fontSize: 40, speed: 90 }
    const { rerender } = render(<MiniLedHost text="X" bar={bar} />)
    const el = screen.getByTestId('mini-led').querySelector<HTMLElement>('.bar')!
    expect(el.style.height).toBe('3cqw')
    expect(el.style.bottom).toBe('0px')
    expect(screen.getByTestId('mini-led').querySelector<HTMLElement>('.tick')!.style.fontSize).toBe('1.5625cqw')
    rerender(<MiniLedHost text={null} bar={bar} />)
    expect(screen.getByTestId('mini-led').querySelector('.bar')).toBeNull()
  })

  it('"reduzir movimento": texto parado e nenhum quadro de animação', () => {
    const raf = vi.fn()
    vi.stubGlobal('requestAnimationFrame', raf)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: q.includes('reduce'),
      addEventListener: () => {},
      removeEventListener: () => {},
    }))
    render(<MiniLedHost text="PARADO" bar={{ position: 'top', height: 56, speed: 90 }} />)
    expect(screen.getByTestId('mini-led').querySelector('.tick')).toHaveClass('still')
    expect(raf).not.toHaveBeenCalled()
  })

  it('desmontar cancela o quadro pendente', () => {
    const cancel = vi.fn()
    vi.stubGlobal('requestAnimationFrame', () => 42)
    vi.stubGlobal('cancelAnimationFrame', cancel)
    const { unmount } = render(<MiniLedHost text="X" bar={{ position: 'top', height: 56, speed: 90 }} />)
    unmount()
    expect(cancel).toHaveBeenCalledWith(42)
  })
})

function MiniLedHost({ text, bar }: { text: string | null; bar: LiveState['bar'] }) {
  return <WithBackend>{<MiniLed text={text} bar={bar} startedAt={undefined} />}</WithBackend>
}


function WithBackend({ children }: { children: ReactNode }) {
  return (
    <AppDataProvider backend={sharedBackend} corrector={null}>
      {children}
    </AppDataProvider>
  )
}
const sharedBackend = new FakeBackend()
