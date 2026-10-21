import { act, fireEvent, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { liveState } from '../test/fakes'
import { renderActive } from '../test/helpers'
import { SendTab } from './SendTab'

const field = () => screen.getByRole('textbox', { name: 'Novo aviso' }) as HTMLTextAreaElement

async function type(text: string) {
  await userEvent.click(field())
  await userEvent.keyboard(text)
}

afterEach(() => vi.useRealTimers())

describe('SendTab: campo e prévia', () => {
  it('vazio mostra a dica e "…"', () => {
    renderActive(<SendTab />)
    expect(screen.getByText('Vai aparecer assim:')).toBeInTheDocument()
    expect(screen.getByText('…', { exact: false })).toBeInTheDocument()
  })

  it('membro sem placa: não vai para o telão e diz o que falta', async () => {
    renderActive(<SendTab />)
    await type('gol branco farol aceso')
    expect(screen.getByText('Não vai para o telão:')).toBeInTheDocument()
    expect(screen.getByText('FALTA PLACA')).toBeInTheDocument()
  })

  it('membro com tudo: prévia no formato do telão', async () => {
    renderActive(<SendTab />)
    await type('prata corolla bra2e19 vidro aberto')
    expect(screen.getByText('Vai aparecer assim:')).toBeInTheDocument()
    expect(screen.getByText('COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO')).toBeInTheDocument()
  })

  it('placa na fila: não precisa mandar', async () => {
    renderActive(<SendTab />, {
      live: liveState({ queue: [{ id: 'a', text: 'GOL BRANCO | PLACA: FFO-4745', source: 'telegram' }] }),
    })
    await type('gol branco ffo4745')
    expect(screen.getByText('Não precisa mandar:')).toBeInTheDocument()
    expect(screen.getByText('ESSE CARRO JÁ ESTÁ NA FILA (GOL BRANCO)')).toBeInTheDocument()
  })

  it('o repetido aparece quando o /live muda depois de digitar', async () => {
    const { backend } = renderActive(<SendTab />)
    await type('gol branco ffo4745')
    expect(screen.queryByText('Não precisa mandar:')).not.toBeInTheDocument()
    backend.emitLive(
      liveState({ now: { id: 'n', text: 'GOL BRANCO | PLACA: FFO-4745', source: 'pc', startedAt: backend.now } }),
    )
    expect(screen.getByText('ESSE CARRO ESTÁ PASSANDO NO TELÃO (GOL BRANCO)')).toBeInTheDocument()
  })

  it('exibido há 9 min: já passou', async () => {
    const { backend } = renderActive(<SendTab />, { live: null })
    backend.emitLive(
      liveState({
        recent: [{ id: 'r', text: 'GOL BRANCO | PLACA: FFO-4745', state: 'shown', at: backend.now - 9 * 60_000 }],
      }),
    )
    await type('gol branco ffo4745')
    expect(screen.getByText('ESSE CARRO JÁ PASSOU HÁ 9 MIN (GOL BRANCO)')).toBeInTheDocument()
  })

  it('admin com repetido é só avisado', async () => {
    renderActive(<SendTab />, {
      role: 'admin',
      live: liveState({ queue: [{ id: 'a', text: 'GOL BRANCO | PLACA: FFO-4745', source: 'telegram' }] }),
    })
    await type('gol branco ffo4745')
    expect(screen.getByText('Atenção, pode mandar mesmo assim:')).toBeInTheDocument()
    expect(screen.getByText('ESSE CARRO JÁ ESTÁ NA FILA (GOL BRANCO)')).toBeInTheDocument()
  })

  it('admin sem placa: selo aviso livre', async () => {
    renderActive(<SendTab />, { role: 'admin' })
    await type('pais do pedro chegou')
    expect(screen.getByText('aviso livre')).toBeInTheDocument()
    expect(screen.getByText('PAIS DO PEDRO CHEGOU')).toBeInTheDocument()
  })

  it('texto colado com quebras de linha e emoji vira uma linha; a prévia funciona', async () => {
    renderActive(<SendTab />)
    await userEvent.click(field())
    await userEvent.paste(`gol branco\nffo4745\n\nfarol aceso ${String.fromCodePoint(0x1f697)}`)
    expect(field().value).toBe(`gol branco ffo4745  farol aceso ${String.fromCodePoint(0x1f697)}`)
    expect(field().value).not.toMatch(/\n/)
    // O formato do telão não tem emoji (igual ao PC).
    expect(screen.getByText('GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO')).toBeInTheDocument()
  })

  it('colar 400 caracteres deixa 300 no campo', async () => {
    renderActive(<SendTab />, { role: 'admin' })
    await userEvent.click(field())
    await userEvent.paste('a'.repeat(400))
    expect(field().value).toHaveLength(300)
  })

  it('Enter não quebra linha e Esc limpa o campo', async () => {
    renderActive(<SendTab />)
    await type('gol branco{Enter}ffo4745')
    expect(field().value).toBe('gol brancoffo4745')
    await userEvent.keyboard('{Escape}')
    expect(field().value).toBe('')
    expect(screen.getByText('Vai aparecer assim:')).toBeInTheDocument()
  })

  it('membro perto do limite vê o contador; acima, "grande demais"', async () => {
    renderActive(<SendTab />)
    await userEvent.click(field())
    await userEvent.paste(`gol branco abc1234 ${'farol aceso '.repeat(13)}`.trimEnd())
    expect(screen.getByText(/^1\d\d\/200$/)).toBeInTheDocument()
    await userEvent.paste(' farol aceso'.repeat(3))
    expect(screen.getByText('TEXTO GRANDE DEMAIS (MÁXIMO 200 LETRAS)')).toBeInTheDocument()
    expect(screen.getByText(/^2\d\d\/200$/)).toHaveClass('over')
  })

  it('texto curto não mostra contador; admin nunca mostra', async () => {
    const { unmount } = renderActive(<SendTab />)
    await type('gol branco abc1234')
    expect(screen.queryByText(/\/200$/)).not.toBeInTheDocument()
    unmount()
    renderActive(<SendTab />, { role: 'admin' })
    await userEvent.click(field())
    await userEvent.paste('a'.repeat(250))
    expect(screen.queryByText(/\/200$/)).not.toBeInTheDocument()
  })

  it('IA recusou: texto não aceito', async () => {
    const corrector = { correct: () => Promise.resolve({ kind: 'free' as const, modelo: '', cor: '', placa: '', motivo: '', texto: '' }) }
    vi.useFakeTimers()
    renderActive(<SendTab />, { role: 'admin', corrector })
    fireEvent.change(field(), { target: { value: 'xingamento qualquer' } })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(700)
    })
    expect(screen.getByText('TEXTO NÃO ACEITO')).toBeInTheDocument()
  })

  it('o (i) abre o balão com as dicas e fecha com Esc; placeholder por papel', async () => {
    const { unmount } = renderActive(<SendTab />)
    expect(field()).toHaveAttribute('placeholder', 'Ex.: corolla prata abc1d23 vidro aberto')
    await userEvent.click(screen.getByRole('button', { name: 'Ajuda' }))
    expect(screen.getByRole('note')).toHaveTextContent('Precisa de modelo, cor e placa.')
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('note')).not.toBeInTheDocument()
    unmount()
    renderActive(<SendTab />, { role: 'admin' })
    expect(field()).toHaveAttribute('placeholder', 'Qualquer aviso…')
    await userEvent.click(screen.getByRole('button', { name: 'Ajuda' }))
    expect(screen.getByRole('note')).toHaveTextContent('Admin pode mandar qualquer texto.')
  })
})
