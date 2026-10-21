import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { FakeAuth, FakeBackend, liveState, TEST_USER, userRecord } from '../test/fakes'
import { Root } from '../ui/Root'
import { TUTORIAL_KEY } from '../ui/Tutorial'
import { readCachedRecord } from './SessionProvider'

beforeEach(() => localStorage.setItem(TUTORIAL_KEY, '1'))

function mount(auth: FakeAuth, backend: FakeBackend) {
  return render(<Root auth={auth} backend={backend} corrector={Promise.resolve(null)} />)
}

const KEY = `alertaibt.record.v1.${TEST_USER.uid}`

describe('registro guardado no aparelho (abrir sem rede)', () => {
  it('guarda só o que a tela usa quando o registro chega', () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord({ role: 'admin', lastSentAt: 5, sentCount: 9 }))
    mount(new FakeAuth(TEST_USER), backend)
    expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual({
      name: TEST_USER.name,
      email: TEST_USER.email,
      role: 'admin',
      blocked: false,
    })
  })

  it('sem rede (o registro não chega) abre o app com o último registro; chegando, o novo vale', () => {
    localStorage.setItem(KEY, JSON.stringify({ name: 'Joana Silva', email: 'joana@example.com', role: 'admin', blocked: false }))
    const backend = new FakeBackend() // /users/{uid} nunca responde
    mount(new FakeAuth(TEST_USER), backend)
    expect(screen.getByRole('tablist')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Equipe' })).toBeInTheDocument() // cargo do último registro
    backend.emitUser(userRecord({ role: 'member' })) // a rede voltou: o cargo atual vale
    expect(screen.queryByRole('tab', { name: 'Equipe' })).not.toBeInTheDocument()
    expect(JSON.parse(localStorage.getItem(KEY)!).role).toBe('member')
  })

  it('o último registro bloqueado abre a tela de bloqueado; liberado no banco, o app volta', () => {
    localStorage.setItem(KEY, JSON.stringify({ name: 'J', email: 'j@x.com', role: 'member', blocked: true }))
    const backend = new FakeBackend()
    mount(new FakeAuth(TEST_USER), backend)
    expect(screen.getByText('Seu acesso foi removido')).toBeInTheDocument()
    backend.emitUser(userRecord())
    expect(screen.getByRole('tablist')).toBeInTheDocument()
  })

  it('registro de outra pessoa não vale; sem nada guardado continua carregando', () => {
    localStorage.setItem('alertaibt.record.v1.outra', JSON.stringify({ name: 'O', email: 'o@x.com', role: 'admin', blocked: false }))
    mount(new FakeAuth(TEST_USER), new FakeBackend())
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
    expect(screen.getByRole('main')).toHaveAttribute('aria-busy', 'true')
  })

  it.each([['lixo', 'não é json'], ['campo errado', JSON.stringify({ name: 'J', email: 'e', role: 'root', blocked: false })], ['sem blocked', JSON.stringify({ name: 'J', email: 'e', role: 'member' })]])(
    'guardado inválido (%s) é ignorado',
    (_name, value) => {
      localStorage.setItem(KEY, value)
      expect(readCachedRecord(TEST_USER.uid)).toBeNull()
      mount(new FakeAuth(TEST_USER), new FakeBackend())
      expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
    },
  )

  it('Sair apaga o registro guardado', async () => {
    const backend = new FakeBackend()
    backend.emitUser(userRecord())
    backend.emitLive(liveState({}, backend.now))
    const auth = new FakeAuth(TEST_USER)
    mount(auth, backend)
    expect(localStorage.getItem(KEY)).not.toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Conta' }))
    await userEvent.click(screen.getByRole('menuitem', { name: 'Sair' }))
    expect(auth.signOutCalls).toBe(1)
    expect(localStorage.getItem(KEY)).toBeNull()
    act(() => auth.emitUser(null))
    expect(screen.getByRole('button', { name: 'Entrar com Google' })).toBeInTheDocument()
  })
})
