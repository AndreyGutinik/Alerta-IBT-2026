import { act, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { formatHm } from '../data/time'
import type { RequestRecord, RequestStatus, WithId } from '../data/types'
import { liveState } from '../test/fakes'
import { renderActive } from '../test/helpers'
import { MyAlerts } from './MyAlerts'

const T0 = 1_800_000_000_000

function req(id: string, status: RequestStatus | undefined, over: Partial<RequestRecord> = {}): WithId<RequestRecord> {
  return {
    id,
    uid: 'u1',
    name: 'Joana Silva',
    raw: `raw ${id}`,
    urgent: false,
    createdAt: T0 - 60_000,
    cancel: false,
    preview: { text: `TEXTO ${id}`, kind: 'vehicle', plate: 'ABC-1234', by: 'local' },
    ...(status ? { status } : {}),
    ...over,
  }
}

const row = (text: string) => screen.getByText(text).closest('li') as HTMLElement

function setup(list: WithId<RequestRecord>[], opts: Parameters<typeof renderActive>[1] = {}) {
  const view = renderActive(<MyAlerts uid="u1" />, opts)
  act(() => view.backend.emitRequests(list))
  return view
}

describe('MyAlerts', () => {
  it('vazio: Nenhum aviso ainda.', () => {
    setup([])
    expect(screen.getByRole('heading', { name: 'Seus avisos' })).toBeInTheDocument()
    expect(screen.getByText('Nenhum aviso ainda.')).toBeInTheDocument()
  })

  it('os estados: na fila (posição), passando, exibido, recusado, cancelado, expirou, enviado', () => {
    setup([
      req('q', { state: 'queued', position: 2, at: T0 }),
      req('n', { state: 'showing', at: T0 }),
      req('o', { state: 'shown', at: new Date(2026, 9, 19, 19, 4).getTime() }),
      req('r', { state: 'rejected', reason: 'faltou placa', at: T0 }),
      req('c', { state: 'cancelled', reason: 'pregação', at: T0 }),
      req('e', { state: 'expired', at: T0 }),
      req('p', undefined),
    ])
    expect(within(row('TEXTO q')).getByText('2º na fila')).toBeInTheDocument()
    expect(within(row('TEXTO n')).getByText('passando')).toBeInTheDocument()
    expect(within(row('TEXTO o')).getByText(formatHm(new Date(2026, 9, 19, 19, 4).getTime()))).toBeInTheDocument()
    expect(within(row('TEXTO o')).getByText('19:04')).toBeInTheDocument()
    expect(within(row('TEXTO r')).getByText('faltou placa')).toBeInTheDocument()
    expect(within(row('TEXTO c')).getByText('cancelado: pregação')).toBeInTheDocument()
    expect(within(row('TEXTO e')).getByText('expirou')).toBeInTheDocument()
    expect(within(row('TEXTO p')).getByText('enviado')).toBeInTheDocument()
  })

  it('cancelado e expirado ficam com o texto riscado (cx); os outros não', () => {
    setup([
      req('c', { state: 'cancelled', at: T0 }),
      req('e', { state: 'expired', at: T0 }),
      req('o', { state: 'shown', at: T0 }),
    ])
    expect(row('TEXTO c')).toHaveClass('cx')
    expect(row('TEXTO e')).toHaveClass('cx')
    expect(row('TEXTO o')).not.toHaveClass('cx')
  })

  it('recusado sem motivo e na fila sem posição', () => {
    setup([req('r', { state: 'rejected', at: T0 }), req('q', { state: 'queued', at: T0 })])
    expect(within(row('TEXTO r')).getByText('recusado')).toBeInTheDocument()
    expect(within(row('TEXTO q')).getByText('na fila')).toBeInTheDocument()
  })

  it('com o telão desligado a posição some (não promete hora)', () => {
    setup([req('q', { state: 'queued', position: 2, at: T0 })], { live: liveState({ led: false }) })
    expect(within(row('TEXTO q')).getByText('na fila')).toBeInTheDocument()
  })

  it('o texto vem do PC, senão da prévia, senão do que foi digitado em maiúsculas', () => {
    setup([
      req('a', { state: 'shown', at: T0, text: 'TEXTO DO PC' }),
      req('b', undefined, { preview: undefined, raw: 'gol branco' }),
    ])
    expect(screen.getByText('TEXTO DO PC')).toBeInTheDocument()
    expect(screen.getByText('GOL BRANCO')).toBeInTheDocument()
  })

  it('Cancelar aviso só em pendente e na fila; clicar chama backend.cancel com o id', async () => {
    const { backend } = setup([
      req('p', undefined),
      req('q', { state: 'queued', position: 1, at: T0 }),
      req('n', { state: 'showing', at: T0 }),
      req('o', { state: 'shown', at: T0 }),
      req('r', { state: 'rejected', at: T0 }),
      req('c', { state: 'cancelled', at: T0 }),
      req('e', { state: 'expired', at: T0 }),
    ])
    expect(screen.getAllByRole('button', { name: 'Cancelar aviso' })).toHaveLength(2)
    const button = within(row('TEXTO q')).getByRole('button', { name: 'Cancelar aviso' })
    expect(button).toHaveAttribute('title', 'Cancelar (o carro já saiu)')
    await userEvent.click(button)
    expect(backend.cancelled).toEqual(['q'])
  })

  it('pedido de cancelamento já gravado: cancelando…, sem botão', () => {
    setup([req('q', { state: 'queued', position: 1, at: T0 }, { cancel: true })])
    expect(screen.getByText('cancelando…')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancelar aviso' })).not.toBeInTheDocument()
  })

  it('cancel que falha: mostra o erro e deixa tentar de novo', async () => {
    const { backend } = setup([req('q', { state: 'queued', position: 1, at: T0 })])
    backend.cancelImpl = () => Promise.reject(new Error('rede'))
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar aviso' }))
    expect(await screen.findByText('Não deu para cancelar. Tente de novo.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancelar aviso' })).toBeEnabled()
  })

  it('toque duplo no X cancela uma vez só', async () => {
    const { backend } = setup([req('q', { state: 'queued', position: 1, at: T0 })])
    backend.cancelImpl = () => new Promise(() => {})
    await userEvent.dblClick(screen.getByRole('button', { name: 'Cancelar aviso' }))
    expect(backend.cancelled).toEqual(['q'])
  })

  it('do mais novo para o mais velho, no máximo 10', () => {
    const list = Array.from({ length: 12 }, (_, i) => req(`r${i}`, { state: 'shown', at: T0 }, { createdAt: T0 + i * 1000 }))
    setup([...list].reverse())
    const rows = screen.getAllByRole('listitem')
    expect(rows).toHaveLength(10)
    expect(rows[0]).toHaveTextContent('TEXTO r11')
    expect(rows[9]).toHaveTextContent('TEXTO r2')
    expect(screen.queryByText('TEXTO r1')).not.toBeInTheDocument()
  })

  it('acompanha as mudanças ao vivo (fila vira passando vira exibido)', () => {
    const { backend } = setup([req('q', { state: 'queued', position: 1, at: T0 })])
    act(() => backend.emitRequests([req('q', { state: 'showing', at: T0 })]))
    expect(screen.getByText('passando')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancelar aviso' })).not.toBeInTheDocument()
    act(() => backend.emitRequests([req('q', { state: 'shown', at: T0 })]))
    expect(screen.getByText(formatHm(T0))).toBeInTheDocument()
  })

  it('sair da tela cancela a assinatura', () => {
    const { backend, unmount } = setup([])
    const before = backend.listenerCount
    unmount()
    expect(backend.listenerCount).toBeLessThan(before)
    expect(backend.listenerCount).toBe(0)
  })
})
