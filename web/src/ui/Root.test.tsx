import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FakeAuth, FakeBackend, TEST_USER, userRecord } from '../test/fakes'
import { Root } from './Root'
import { TUTORIAL_KEY } from './Tutorial'

function setup(auth = new FakeAuth(null), backend = new FakeBackend()) {
  render(<Root auth={auth} backend={backend} corrector={Promise.resolve(null)} />)
  return { auth, backend }
}

beforeEach(() => localStorage.setItem(TUTORIAL_KEY, '1'))
afterEach(() => {
  localStorage.clear()
  vi.useRealTimers()
})

describe('Root', () => {
  it('sem login mostra Entrar com Google e chama signIn', async () => {
    const { auth } = setup()
    await userEvent.click(screen.getByRole('button', { name: 'Entrar com Google' }))
    expect(auth.signInCalls).toBe(1)
  })

  it('login cancelado mostra a mensagem', async () => {
    const auth = new FakeAuth(null)
    auth.signInImpl = () => Promise.reject({ code: 'auth/popup-closed-by-user' })
    setup(auth)
    await userEvent.click(screen.getByRole('button', { name: 'Entrar com Google' }))
    expect(await screen.findByText('Login cancelado.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Entrar com Google' })).toBeEnabled()
  })

  it('depois do login garante o registro e mostra o app', async () => {
    const { auth, backend } = setup()
    act(() => auth.emitUser(TEST_USER))
    expect(backend.ensured).toEqual([TEST_USER])
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
    backend.emitUser(userRecord())
    expect(await screen.findByRole('tablist')).toBeInTheDocument()
  })

  it('erro do ensureUser é ignorado', async () => {
    const backend = new FakeBackend()
    backend.ensureUserImpl = () => Promise.reject(new Error('permission_denied'))
    const { auth } = setup(new FakeAuth(null), backend)
    act(() => auth.emitUser(TEST_USER))
    backend.emitUser(userRecord())
    expect(await screen.findByRole('tablist')).toBeInTheDocument()
  })

  it('a mesma pessoa de novo não derruba o app', () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord())
    const { auth } = setup(new FakeAuth(TEST_USER), backend)
    expect(screen.getByRole('tablist')).toBeInTheDocument()
    act(() => auth.emitUser({ ...TEST_USER }))
    expect(screen.getByRole('tablist')).toBeInTheDocument()
  })

  it('registro bloqueado mostra o aviso e o botão Sair', async () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord({ blocked: true }))
    const { auth } = setup(new FakeAuth(TEST_USER), backend)
    expect(screen.getByText('Seu acesso foi removido')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Sair' }))
    expect(auth.signOutCalls).toBe(1)
  })

  it('bloqueado no meio do uso troca de tela', () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord())
    setup(new FakeAuth(TEST_USER), backend)
    expect(screen.getByRole('tablist')).toBeInTheDocument()
    backend.emitUser(userRecord({ blocked: true }))
    expect(screen.getByText('Seu acesso foi removido')).toBeInTheDocument()
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
  })

  it('desbloqueado volta ao app', () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord({ blocked: true }))
    setup(new FakeAuth(TEST_USER), backend)
    backend.emitUser(userRecord())
    expect(screen.getByRole('tablist')).toBeInTheDocument()
  })

  it('carregando demorado explica e oferece Sair', () => {
    vi.useFakeTimers()
    setup(new FakeAuth(TEST_USER))
    expect(screen.queryByText('Está demorando. Confira a internet.')).not.toBeInTheDocument()
    act(() => {
      vi.advanceTimersByTime(10_000)
    })
    expect(screen.getByText('Está demorando. Confira a internet.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sair' })).toBeInTheDocument()
  })

  it('sair desinscreve /live e a conexão', () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord())
    const auth = new FakeAuth(TEST_USER)
    setup(auth, backend)
    expect(backend.listenerCount).toBeGreaterThan(1)
    act(() => auth.emitUser(null))
    expect(screen.getByRole('button', { name: 'Entrar com Google' })).toBeInTheDocument()
    expect(backend.listenerCount).toBe(0)
  })
})
