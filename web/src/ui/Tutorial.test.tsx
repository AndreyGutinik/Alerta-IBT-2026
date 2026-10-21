import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FakeAuth, FakeBackend, TEST_USER, userRecord } from '../test/fakes'
import { Root } from './Root'
import { TUTORIAL_KEY } from './Tutorial'

function setup() {
  const backend = new FakeBackend()
  backend.emitUser(userRecord())
  render(<Root auth={new FakeAuth(TEST_USER)} backend={backend} corrector={Promise.resolve(null)} />)
}

afterEach(() => {
  localStorage.clear()
  vi.restoreAllMocks()
})

describe('Tutorial', () => {
  it('primeiro acesso: 3 passos e Começar grava a chave', async () => {
    setup()
    expect(screen.getByRole('heading', { name: 'Como mandar um aviso' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Próximo/ }))
    expect(screen.getByRole('heading', { name: 'O que não pode faltar' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Próximo/ }))
    expect(screen.getByRole('heading', { name: 'Acompanhe e urgente' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Começar/ }))
    expect(screen.getByRole('tablist')).toBeInTheDocument()
    expect(localStorage.getItem(TUTORIAL_KEY)).toBe('1')
  })

  it('Pular no primeiro passo também conclui', async () => {
    setup()
    await userEvent.click(screen.getByRole('button', { name: 'Pular' }))
    expect(screen.getByRole('tablist')).toBeInTheDocument()
    expect(localStorage.getItem(TUTORIAL_KEY)).toBe('1')
  })

  it('com a chave gravada o tutorial não aparece', () => {
    localStorage.setItem(TUTORIAL_KEY, '1')
    setup()
    expect(screen.queryByText('Como mandar um aviso')).not.toBeInTheDocument()
    expect(screen.getByRole('tablist')).toBeInTheDocument()
  })

  it('localStorage que lança erro não quebra', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('bloqueado')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('bloqueado')
    })
    setup()
    expect(screen.getByText('Como mandar um aviso')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Pular' }))
    expect(screen.queryByText('Como mandar um aviso')).not.toBeInTheDocument()
    expect(screen.getByRole('tablist')).toBeInTheDocument()
  })
})
