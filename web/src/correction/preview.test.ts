import { describe, expect, it, vi } from 'vitest'
import type { LiveState } from '../data/types'
import { type PreviewContext, buildPreview } from './preview'
import type { AiAlert, AiCorrector } from './types'

const NOW = 5_000_000
const ctx = (extra: Partial<PreviewContext> = {}): PreviewContext => ({ role: 'member', live: null, serverNow: NOW, ...extra })
const fake = (answer: AiAlert | null): AiCorrector => ({ correct: vi.fn(() => Promise.resolve(answer)) })
const live = (partial: Partial<LiveState> = {}): LiveState => ({
  heartbeatAt: NOW,
  led: true,
  paused: false,
  now: null,
  queue: [],
  recent: [],
  bar: { position: 'top', height: 56, speed: 90 },
  ...partial,
})

describe('buildPreview', () => {
  it('membro com tudo: aviso de veículo corrigido pela IA, com a placa', async () => {
    const corrector = fake({ kind: 'vehicle', modelo: 'Corolla', cor: 'prata', placa: 'BRA2E19', motivo: 'vidro aberto', texto: '' })

    const preview = await buildPreview('prata corolla bra2e19 vidro abrto', corrector, ctx())

    expect(preview).toEqual({
      text: 'COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO',
      kind: 'vehicle',
      missing: [],
      by: 'gemini',
      plate: 'BRA-2E19',
    })
  })

  it('sem IA: o parser local, by local', async () => {
    const preview = await buildPreview('gol branco ffo4745 farol aceso', null, ctx())

    expect(preview).toMatchObject({ text: 'GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO', kind: 'vehicle', by: 'local' })
  })

  it('IA fora da trava: o parser local decide', async () => {
    const corrector = fake({ kind: 'vehicle', modelo: 'Gol', cor: 'prata', placa: 'FFO4745', motivo: '', texto: '' })

    const preview = await buildPreview('gol branco ffo4745', corrector, ctx())

    expect(preview).toMatchObject({ by: 'local', text: 'GOL BRANCO | PLACA: FFO-4745' })
  })

  it('membro sem placa: não vai, diz o que falta', async () => {
    const preview = await buildPreview('gol branco farol aceso', null, ctx())

    expect(preview).toEqual({ text: '', kind: 'reject', missing: ['placa'], by: 'local' })
  })

  it('membro sem modelo e cor: lista os dois, na ordem', async () => {
    expect((await buildPreview('abc1234', null, ctx())).missing).toEqual(['modelo', 'cor'])
  })

  it('admin sem placa: aviso livre em maiúsculas', async () => {
    const preview = await buildPreview('pais do pedro henrique, favor comparecer na recepção', null, ctx({ role: 'admin' }))

    expect(preview).toMatchObject({ kind: 'free', text: 'PAIS DO PEDRO HENRIQUE, FAVOR COMPARECER NA RECEPÇÃO', by: 'local' })
    expect(preview.plate).toBeUndefined()
  })

  it('admin com placa e sem cor: aviso de veículo', async () => {
    const preview = await buildPreview('corolla abc1234 vidro aberto', null, ctx({ role: 'admin' }))

    expect(preview).toMatchObject({ kind: 'vehicle', missing: ['cor'], text: 'COROLLA | PLACA: ABC-1234 | VIDRO ABERTO' })
  })

  it('texto grande demais de membro: recusa antes de qualquer correção (a IA nem é chamada)', async () => {
    const corrector = fake(null)
    const raw = `gol branco abc1234 ${'farol aceso '.repeat(20)}`

    const preview = await buildPreview(raw, corrector, ctx())

    expect(preview).toMatchObject({ kind: 'reject', tooLong: true, text: '' })
    expect(corrector.correct).not.toHaveBeenCalled()
  })

  it('o mesmo texto grande passa para admin e operador', async () => {
    const raw = `gol branco abc1234 ${'farol aceso '.repeat(20)}`

    expect((await buildPreview(raw, null, ctx({ role: 'admin' }))).kind).toBe('vehicle')
    expect((await buildPreview(raw, null, ctx({ role: 'operator' }))).tooLong).toBeUndefined()
  })

  it('recusa da IA: nota neutra, sem texto, sem cair no local', async () => {
    const preview = await buildPreview('texto impróprio', fake({ kind: 'free', modelo: '', cor: '', placa: '', motivo: '', texto: '' }), ctx())

    expect(preview).toEqual({ text: '', kind: 'reject', missing: [], by: 'gemini', refused: true })
  })

  it('mesma placa na fila: duplicate com o rótulo', async () => {
    const queue = [{ id: 'q', text: 'GOL BRANCO | PLACA: FFO-4745', source: 'site' as const }]

    const preview = await buildPreview('gol branco ffo4745', null, ctx({ live: live({ queue }) }))

    expect(preview.duplicate).toEqual({ where: 'queue', label: 'GOL BRANCO' })
    expect(preview.kind).toBe('vehicle')
  })

  it('exibido há 11 min não é repetido; aviso livre nunca é', async () => {
    const recent = [{ id: 'r', text: 'GOL BRANCO | PLACA: FFO-4745', state: 'shown' as const, at: NOW - 11 * 60_000 }]

    expect((await buildPreview('gol branco ffo4745', null, ctx({ live: live({ recent }) }))).duplicate).toBeUndefined()
    expect((await buildPreview('aviso qualquer', null, ctx({ role: 'admin', live: live({ recent }) }))).duplicate).toBeUndefined()
  })

  it('a IA estoura o tempo: o local responde e o signal é abortado', async () => {
    vi.useFakeTimers()
    try {
      let signal: AbortSignal | undefined
      const silent: AiCorrector = {
        correct: (_raw, s) => {
          signal = s
          return new Promise(() => undefined)
        },
      }

      const pending = buildPreview('gol branco ffo4745', silent, ctx({ timeoutMs: 1000 }))
      await vi.advanceTimersByTimeAsync(1000)

      expect((await pending).by).toBe('local')
      expect(signal?.aborted).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})
