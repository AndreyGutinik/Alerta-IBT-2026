import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FakeAuth, FakeBackend, liveState, TEST_USER, userRecord } from '../test/fakes'
import { Root } from './Root'
import { TUTORIAL_KEY } from './Tutorial'

beforeEach(() => localStorage.setItem(TUTORIAL_KEY, '1'))
afterEach(() => {
  localStorage.clear()
  vi.useRealTimers()
})

function setup(role: 'member' | 'admin' = 'member') {
  const backend = new FakeBackend()
  const auth = new FakeAuth(TEST_USER)
  backend.emitUser(userRecord({ role }))
  backend.emitLive(liveState({}, backend.now))
  const view = render(<Root auth={auth} backend={backend} corrector={Promise.resolve(null)} />)
  return { backend, auth, ...view }
}

describe('Shell', () => {
  it('membro: abas Enviar e Status, sem Equipe; títulos trocam', async () => {
    setup()
    expect(screen.getByRole('tab', { name: 'Enviar' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Status' })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: 'Equipe' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Enviar' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Status' }))
    expect(screen.getByRole('heading', { name: 'Agora' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Status' })).toHaveAttribute('aria-selected', 'true')
  })

  it('o avatar mostra a inicial e abre o menu com Sair', async () => {
    const { auth } = setup()
    const avatar = screen.getByRole('button', { name: 'Conta' })
    expect(avatar).toHaveTextContent('J')
    expect(avatar.querySelector('.avatar')).not.toHaveClass('adm')
    await userEvent.click(avatar)
    await userEvent.click(screen.getByRole('menuitem', { name: 'Sair' }))
    expect(auth.signOutCalls).toBe(1)
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('Esc fecha o menu', async () => {
    setup()
    await userEvent.click(screen.getByRole('button', { name: 'Conta' }))
    expect(screen.getByRole('menu')).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('admin: aba Equipe, ponto amarelo e o avatar abre a Equipe', async () => {
    setup('admin')
    expect(screen.getByRole('tab', { name: 'Equipe' })).toBeInTheDocument()
    const avatar = screen.getByRole('button', { name: 'Equipe' })
    expect(avatar.querySelector('.avatar')).toHaveClass('adm')
    await userEvent.click(avatar)
    expect(screen.getByRole('heading', { name: 'Equipe' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Sair' })).toBeInTheDocument()
  })

  it('promovido no meio do uso: Equipe aparece; rebaixado na aba Equipe volta para Enviar', async () => {
    const { backend } = setup()
    expect(screen.queryByRole('tab', { name: 'Equipe' })).not.toBeInTheDocument()
    backend.emitUser(userRecord({ role: 'admin' }))
    await userEvent.click(screen.getByRole('tab', { name: 'Equipe' }))
    expect(screen.getByRole('heading', { name: 'Equipe' })).toBeInTheDocument()
    backend.emitUser(userRecord({ role: 'member' }))
    expect(screen.queryByRole('tab', { name: 'Equipe' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Enviar' })).toBeInTheDocument()
  })

  describe('pílula do cabeçalho', () => {
    it('Telão ligado', () => {
      setup()
      expect(screen.getByText('Telão ligado')).toBeInTheDocument()
    })

    it('Sem conexão', () => {
      const { backend } = setup()
      backend.setConnected(false)
      expect(screen.getByText('Sem conexão')).toBeInTheDocument()
    })

    it('PC offline (heartbeat de 91 s)', () => {
      const { backend } = setup()
      backend.emitLive(liveState({ heartbeatAt: backend.now - 91_000 }, backend.now))
      expect(screen.getByText('PC offline')).toBeInTheDocument()
    })

    it('Barra pausada', () => {
      const { backend } = setup()
      backend.emitLive(liveState({ paused: true }, backend.now))
      expect(screen.getByText('Barra pausada')).toBeInTheDocument()
    })

    it('Telão desligado', () => {
      const { backend } = setup()
      backend.emitLive(liveState({ led: false }, backend.now))
      expect(screen.getByText('Telão desligado')).toBeInTheDocument()
    })

    it('só o tempo passando (sem novo /live) vira PC offline', () => {
      vi.useFakeTimers()
      const { backend } = setup()
      expect(screen.getByText('Telão ligado')).toBeInTheDocument()
      backend.now += 91_000
      act(() => {
        vi.advanceTimersByTime(1000)
      })
      expect(screen.getByText('PC offline')).toBeInTheDocument()
    })
  })
})
