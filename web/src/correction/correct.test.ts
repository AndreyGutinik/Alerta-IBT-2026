import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AI_TIMEOUT_MS, correctAlert } from './correct'
import type { AiAlert, AiCorrector } from './types'

const GOOD: AiAlert = { kind: 'vehicle', modelo: 'Gol', cor: 'branco', placa: 'FFO4745', motivo: '', texto: '' }
const RAW = 'gol branco ffo4745'

beforeEach(() => {
  vi.useFakeTimers()
})
afterEach(() => {
  vi.useRealTimers()
})

describe('correctAlert', () => {
  it('o tempo máximo da IA é de 5 s, como no PC', () => {
    expect(AI_TIMEOUT_MS).toBe(5000)
  })

  it('sem corretor: local, na hora', async () => {
    const result = await correctAlert(RAW, null)

    expect(result).toMatchObject({ by: 'local', refused: false })
    expect(result.alert.plate).toBe('FFO-4745')
  })

  it('texto em branco não chama a IA', async () => {
    const correct = vi.fn(() => Promise.resolve(GOOD))

    const result = await correctAlert('   ', { correct })

    expect(correct).not.toHaveBeenCalled()
    expect(result.by).toBe('local')
  })

  it('corretor que lança (ou rejeita): local', async () => {
    const throws: AiCorrector = {
      correct: () => {
        throw new Error('quebrou')
      },
    }
    const rejects: AiCorrector = { correct: () => Promise.reject(new Error('sem rede')) }

    expect((await correctAlert(RAW, throws)).by).toBe('local')
    expect((await correctAlert(RAW, rejects)).by).toBe('local')
  })

  it('corretor que devolve null: local', async () => {
    expect((await correctAlert(RAW, { correct: () => Promise.resolve(null) })).by).toBe('local')
  })

  it('corretor que não responde: depois do tempo máximo, local, e o signal foi abortado', async () => {
    let received: AbortSignal | undefined
    const silent: AiCorrector = {
      correct: (_raw, signal) => {
        received = signal
        return new Promise(() => undefined)
      },
    }

    const pending = correctAlert(RAW, silent)
    await vi.advanceTimersByTimeAsync(AI_TIMEOUT_MS - 1)
    expect(received?.aborted).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    const result = await pending

    expect(result.by).toBe('local')
    expect(received?.aborted).toBe(true)
  })

  it('tempo máximo configurável', async () => {
    const silent: AiCorrector = { correct: () => new Promise(() => undefined) }

    const pending = correctAlert(RAW, silent, 3000)
    await vi.advanceTimersByTimeAsync(3000)

    expect((await pending).by).toBe('local')
  })

  it('resposta que chega depois do tempo é ignorada, sem rejeição sem dono', async () => {
    let finish: (value: AiAlert | null) => void = () => undefined
    const late: AiCorrector = { correct: () => new Promise((resolve) => (finish = resolve)) }

    const pending = correctAlert(RAW, late, 1000)
    await vi.advanceTimersByTimeAsync(1000)
    finish(GOOD)

    expect((await pending).by).toBe('local')
  })

  it('corretor ok: gemini, e o timer é limpo', async () => {
    const result = await correctAlert(RAW, { correct: () => Promise.resolve(GOOD) })

    expect(result).toMatchObject({ by: 'gemini', refused: false })
    expect(result.alert).toMatchObject({ model: 'Gol', color: 'branco', plate: 'FFO-4745' })
    expect(vi.getTimerCount()).toBe(0)
  })

  it('resposta que a trava recusa: local', async () => {
    const wrongPlate: AiAlert = { ...GOOD, placa: 'ABC9999' }

    expect((await correctAlert(RAW, { correct: () => Promise.resolve(wrongPlate) })).by).toBe('local')
  })

  it('recusa da IA (texto ofensivo): refused, gemini', async () => {
    const refusal: AiAlert = { kind: 'free', modelo: '', cor: '', placa: '', motivo: '', texto: '' }

    expect(await correctAlert('xingamento', { correct: () => Promise.resolve(refusal) })).toMatchObject({
      by: 'gemini',
      refused: true,
    })
  })

  it('o corretor recebe o texto digitado', async () => {
    const correct = vi.fn((_raw: string, _signal: AbortSignal) => Promise.resolve(GOOD))

    await correctAlert(RAW, { correct })

    expect(correct).toHaveBeenCalledWith(RAW, expect.any(AbortSignal))
  })
})
