import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiAlert, AiCorrector } from '../correction/types'
import { AppDataProvider } from '../state/AppData'
import { FakeBackend, liveState } from '../test/fakes'
import { usePreview } from './usePreview'

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

function controllable() {
  const calls: { raw: string; resolve: (a: AiAlert | null) => void }[] = []
  const corrector: AiCorrector = {
    correct: (raw) => new Promise<AiAlert | null>((resolve) => calls.push({ raw, resolve })),
  }
  return { corrector, calls }
}

const ai = (over: Partial<AiAlert>): AiAlert => ({
  kind: 'vehicle',
  modelo: 'Gol',
  cor: 'branco',
  placa: 'FFO4745',
  motivo: '',
  texto: '',
  ...over,
})

function setup(initial: { raw: string; corrector: AiCorrector | null }) {
  const backend = new FakeBackend()
  backend.emitLive(liveState({}, backend.now))
  const wrapper = ({ children }: { children: ReactNode }) => (
    <AppDataProvider backend={backend} corrector={null}>
      {children}
    </AppDataProvider>
  )
  return renderHook(({ raw, corrector }) => usePreview(raw, 'member', corrector), {
    wrapper,
    initialProps: initial,
  })
}

const tick = (ms: number) =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })

describe('usePreview', () => {
  it('sem corretor: pronta na hora com o parser local', () => {
    const { result, rerender } = setup({ raw: '', corrector: null })
    expect(result.current.status).toBe('empty')
    rerender({ raw: 'gol branco ffo4745', corrector: null })
    expect(result.current.status).toBe('ready')
    expect(result.current.result).toMatchObject({ text: 'GOL BRANCO | PLACA: FFO-4745', by: 'local', canSend: true })
  })

  it('texto em branco volta a vazio', () => {
    const { result, rerender } = setup({ raw: 'gol', corrector: null })
    rerender({ raw: '   ', corrector: null })
    expect(result.current).toEqual({ status: 'empty', result: null })
  })

  it('com corretor: ocupada, só chama depois de 700 ms parado e fica pronta com by gemini', async () => {
    const { corrector, calls } = controllable()
    const { result, rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'gol branco ffo4745', corrector })
    expect(result.current.status).toBe('busy')
    expect(result.current.result).toBeNull()

    await tick(699)
    expect(calls).toHaveLength(0)
    await tick(1)
    expect(calls).toHaveLength(1)
    expect(result.current.status).toBe('busy')

    await act(async () => calls[0].resolve(ai({})))
    expect(result.current.status).toBe('ready')
    expect(result.current.result).toMatchObject({ by: 'gemini', text: 'GOL BRANCO | PLACA: FFO-4745' })
  })

  it('digitar de novo antes dos 700 ms reinicia a espera (uma chamada só)', async () => {
    const { corrector, calls } = controllable()
    const { rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'gol', corrector })
    await tick(500)
    rerender({ raw: 'gol branco', corrector })
    await tick(500)
    expect(calls).toHaveLength(0)
    await tick(200)
    expect(calls.map((c) => c.raw)).toEqual(['gol branco'])
  })

  it('respostas fora de ordem: a prévia é sempre a do texto mais novo', async () => {
    const { corrector, calls } = controllable()
    const { result, rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'gol branco ffo4745', corrector })
    await tick(700)
    rerender({ raw: 'gol branco ffo4745 farol aceso', corrector })
    await tick(700)
    expect(calls).toHaveLength(2)

    await act(async () => calls[1].resolve(ai({ motivo: 'farol aceso' })))
    expect(result.current.result?.text).toBe('GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO')
    await act(async () => calls[0].resolve(ai({})))
    expect(result.current.result?.text).toBe('GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO')
  })

  it('resposta velha que chega enquanto a nova ainda espera não vira prévia', async () => {
    const { corrector, calls } = controllable()
    const { result, rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'gol branco ffo4745', corrector })
    await tick(700)
    rerender({ raw: 'gol branco ffo4745 farol aceso', corrector })
    await act(async () => calls[0].resolve(ai({})))
    expect(result.current.status).toBe('busy')
    expect(result.current.result).toBeNull()
  })

  it('IA que falha cai no parser local', async () => {
    const { corrector, calls } = controllable()
    const { result, rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'gol branco ffo4745', corrector })
    await tick(700)
    await act(async () => calls[0].resolve(null))
    expect(result.current.result).toMatchObject({ by: 'local', canSend: true })
  })

  it('membro com texto enorme: grande demais na hora, sem chamar a IA', async () => {
    const { corrector, calls } = controllable()
    const { result, rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'a'.repeat(250), corrector })
    expect(result.current.status).toBe('ready')
    expect(result.current.result).toMatchObject({ tooLong: true, canSend: false })
    await tick(2000)
    expect(calls).toHaveLength(0)
  })

  it('IA que demora mais que o tempo máximo cai no local', async () => {
    const { corrector, calls } = controllable()
    const { result, rerender } = setup({ raw: '', corrector })
    rerender({ raw: 'gol branco ffo4745', corrector })
    await tick(700)
    expect(calls).toHaveLength(1)
    await tick(5000)
    expect(result.current.result).toMatchObject({ by: 'local' })
  })
})
