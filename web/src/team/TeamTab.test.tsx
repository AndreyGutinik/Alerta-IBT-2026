import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { formatDayTime } from '../data/time'
import { FakeBackend, TEST_USER, userRecord } from '../test/fakes'
import { renderActive } from '../test/helpers'
import { OWNER_EMAIL } from '../session/owner'
import { MSG_OWNER, MSG_TEAM_FAILED, TeamTab } from './TeamTab'
import type { Member } from './members'

const DAY = 86_400_000
const backendNow = new FakeBackend().now

function member(id: string, over: Partial<Member> = {}): Member {
  return {
    id,
    ...userRecord({ name: `Pessoa ${id}`, email: `${id}@example.com`, createdAt: backendNow - 30 * DAY }),
    ...over,
  }
}

const SELF = member('u1', { name: TEST_USER.name, email: TEST_USER.email, role: 'admin', lastSeenAt: backendNow - 1000 })
const ANA = member('ana', { name: 'Ana', lastSeenAt: backendNow - 3 * 3600_000, sentCount: 4, createdAt: backendNow - 2 * DAY })
const BETO = member('beto', { name: 'Beto', lastSeenAt: backendNow - 5 * DAY, createdAt: backendNow - 10 * DAY, sentCount: 1 })
const CIDA = member('cida', { name: 'Cida', blocked: true, lastSeenAt: backendNow - 9 * DAY })

function setup(list: Member[] | null = [BETO, SELF, CIDA, ANA], backend = new FakeBackend()) {
  const view = renderActive(<TeamTab />, { role: 'admin', backend })
  if (list) backend.emitUsers(list)
  return { ...view, backend }
}

const row = (name: string) => screen.getByText(name, { selector: '.mem-n' }).closest('li')!

describe('TeamTab', () => {
  it('lista por último uso decrescente, com e-mail, último uso e total de avisos', () => {
    setup()
    const names = screen.getAllByRole('listitem').map((li) => li.querySelector('.mem-n')?.textContent)
    expect(names).toEqual([TEST_USER.name, 'Ana', 'Beto', 'Cida'])
    expect(within(row('Ana')).getByText('ana@example.com')).toBeInTheDocument()
    expect(within(row('Ana')).getByText(`Último uso: ${formatDayTime(ANA.lastSeenAt!)} · 4 avisos`)).toBeInTheDocument()
    expect(within(row('Beto')).getByText(/· 1 aviso$/)).toBeInTheDocument()
    expect(within(row('Cida')).getByText(/· 0 avisos$/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Quem já entrou (4)' })).toBeInTheDocument()
  })

  it('selo novo só para quem entrou há menos de 7 dias; admin, removido e você', () => {
    setup()
    expect(within(row('Ana')).getByText('novo')).toBeInTheDocument()
    expect(within(row('Beto')).queryByText('novo')).not.toBeInTheDocument()
    expect(within(row('Cida')).getByText('removido')).toBeInTheDocument()
    expect(within(row(TEST_USER.name)).getByText('você')).toBeInTheDocument()
    expect(within(row(TEST_USER.name)).getByText('admin')).toBeInTheDocument()
    expect(within(row('Ana')).queryByText('admin')).not.toBeInTheDocument()
  })

  it('a própria linha não tem botões', () => {
    setup()
    expect(within(row(TEST_USER.name)).queryAllByRole('button')).toHaveLength(0)
  })

  it('Tornar admin / Tirar admin chamam setRole', async () => {
    const other = member('dora', { name: 'Dora', role: 'admin' })
    const { backend } = setup([SELF, ANA, other])
    await userEvent.click(within(row('Ana')).getByRole('button', { name: 'Tornar admin' }))
    await userEvent.click(within(row('Dora')).getByRole('button', { name: 'Tirar admin' }))
    expect(backend.roleChanges).toEqual([
      { uid: 'ana', role: 'admin' },
      { uid: 'dora', role: 'member' },
    ])
  })

  it('Remover bloqueia e Restaurar desbloqueia', async () => {
    const { backend } = setup()
    await userEvent.click(within(row('Beto')).getByRole('button', { name: 'Remover' }))
    await userEvent.click(within(row('Cida')).getByRole('button', { name: 'Restaurar' }))
    expect(backend.blockChanges).toEqual([
      { uid: 'beto', blocked: true },
      { uid: 'cida', blocked: false },
    ])
  })

  it('quem está removido não ganha Tornar admin', () => {
    setup()
    expect(within(row('Cida')).queryByRole('button', { name: 'Tornar admin' })).not.toBeInTheDocument()
  })

  it('não rebaixa nem remove o último admin ativo (lista sem o outro admin ativo)', async () => {
    // O próprio SELF ainda não chegou na lista (ou foi removido): Dora é o único admin ativo.
    const dora = member('dora', { name: 'Dora', role: 'admin' })
    const { backend } = setup([dora, ANA])
    const r = row('Dora')
    expect(within(r).getByRole('button', { name: 'Tirar admin' })).toBeDisabled()
    expect(within(r).getByRole('button', { name: 'Remover' })).toBeDisabled()
    await userEvent.click(within(r).getByRole('button', { name: 'Tirar admin' }))
    await userEvent.click(within(r).getByRole('button', { name: 'Remover' }))
    expect(backend.roleChanges).toEqual([])
    expect(backend.blockChanges).toEqual([])
  })

  it('com outro admin ativo na lista dá para tirar o admin; admin removido não conta como ativo', () => {
    const dora = member('dora', { name: 'Dora', role: 'admin' })
    const zed = member('zed', { name: 'Zed', role: 'admin', blocked: true })
    setup([SELF, dora, zed])
    expect(within(row('Dora')).getByRole('button', { name: 'Tirar admin' })).toBeEnabled()
    // Zed está removido: dá para tirar o admin dele, e ele não conta para proteger ninguém
    expect(within(row('Zed')).getByRole('button', { name: 'Tirar admin' })).toBeEnabled()
    expect(within(row('Zed')).getByRole('button', { name: 'Restaurar' })).toBeEnabled()
  })

  it('erro em setRole mostra o aviso e libera o botão', async () => {
    const backend = new FakeBackend()
    backend.setRoleImpl = () => Promise.reject(new Error('permission_denied'))
    setup([SELF, ANA], backend)
    await userEvent.click(within(row('Ana')).getByRole('button', { name: 'Tornar admin' }))
    expect(await screen.findByText(MSG_TEAM_FAILED)).toBeInTheDocument()
    expect(MSG_TEAM_FAILED).toBe('Não deu certo. Tente de novo.')
    expect(within(row('Ana')).getByRole('button', { name: 'Tornar admin' })).toBeEnabled()
  })

  it('erro em setBlocked também avisa', async () => {
    const backend = new FakeBackend()
    backend.setBlockedImpl = () => Promise.reject(new Error('x'))
    setup([SELF, ANA], backend)
    await userEvent.click(within(row('Ana')).getByRole('button', { name: 'Remover' }))
    expect(await screen.findByText(MSG_TEAM_FAILED)).toBeInTheDocument()
  })

  it('toque duplo: um pedido só enquanto o primeiro não terminou', async () => {
    const backend = new FakeBackend()
    backend.setBlockedImpl = () => new Promise(() => {})
    setup([SELF, ANA], backend)
    const button = within(row('Ana')).getByRole('button', { name: 'Remover' })
    await userEvent.dblClick(button)
    expect(backend.blockChanges).toHaveLength(1)
    expect(button).toBeDisabled()
  })

  it('Sair chama auth.signOut', async () => {
    const { auth } = setup()
    await userEvent.click(screen.getByRole('button', { name: 'Sair' }))
    expect(auth.signOutCalls).toBe(1)
  })

  it('carregando, vazio e desmontar cancela a assinatura', () => {
    const { backend, unmount } = setup(null)
    expect(screen.getByText('Carregando…')).toBeInTheDocument()
    backend.emitUsers([])
    expect(screen.getByText('Ninguém entrou ainda.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sair' })).toBeInTheDocument()
    unmount()
    expect(backend.listenerCount).toBe(0)
  })

  describe('o mantenedor', () => {
    const OWNER = member('dono', { name: 'Dono', email: OWNER_EMAIL, role: 'admin', lastSeenAt: backendNow - 2000 })

    it('mostra o selo Mantenedor do app e nenhum botão de remover, rebaixar ou restaurar', () => {
      setup([SELF, OWNER, ANA])
      const li = row('Dono')
      expect(within(li).getByText('Mantenedor do app')).toBeInTheDocument()
      expect(within(li).queryByText('admin')).not.toBeInTheDocument()
      for (const name of ['Remover', 'Restaurar', 'Tirar admin', 'Tornar admin']) {
        expect(within(li).queryByRole('button', { name })).not.toBeInTheDocument()
      }
      // só o (i) da explicação, e a linha de uma pessoa comum segue com os botões
      expect(within(li).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Sobre o mantenedor'])
      expect(within(row('Ana')).getByRole('button', { name: 'Remover' })).toBeInTheDocument()
    })

    it('o (i) explica que o mantenedor não pode ser removido nem deixar de ser administrador', async () => {
      setup([SELF, OWNER])
      await userEvent.click(within(row('Dono')).getByRole('button', { name: 'Sobre o mantenedor' }))
      expect(screen.getByRole('note')).toHaveTextContent(MSG_OWNER)
      expect(MSG_OWNER).toBe('Mantém o app. Não pode ser removido nem deixar de ser administrador.')
    })

    it('o selo vale pelo e-mail em outra caixa e mesmo com registro bloqueado ou member (sem selo de removido)', () => {
      const odd = member('dono', { name: 'Dono', email: OWNER_EMAIL.toUpperCase(), role: 'member', blocked: true })
      setup([SELF, odd])
      const li = row('Dono')
      expect(within(li).getByText('Mantenedor do app')).toBeInTheDocument()
      expect(within(li).queryByText('removido')).not.toBeInTheDocument()
      expect(within(li).queryByRole('button', { name: 'Restaurar' })).not.toBeInTheDocument()
    })

    it('Sair continua disponível', () => {
      setup([SELF, OWNER])
      expect(screen.getByRole('button', { name: 'Sair' })).toBeInTheDocument()
    })
  })
})
