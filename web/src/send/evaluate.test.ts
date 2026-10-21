import { describe, expect, it } from 'vitest'
import { parseAlert } from '../alerts/parser'
import type { Role } from '../alerts/types'
import type { Correction } from '../correction/types'
import type { LiveState } from '../data/types'
import { liveState } from '../test/fakes'
import { evaluatePreview, isRawTooLong, tooLongResult } from './evaluate'

const NOW = 1_800_000_000_000
const local = (raw: string): Correction => ({ alert: parseAlert(raw), by: 'local', refused: false })
const run = (raw: string, role: Role, live: LiveState | null = liveState({}, NOW)) =>
  evaluatePreview(local(raw), role, live, NOW)

const queued = liveState(
  { queue: [{ id: 'a', text: 'GOL BRANCO | PLACA: FFO-4745', source: 'telegram' }] },
  NOW,
)

describe('evaluatePreview', () => {
  it('membro com modelo, cor e placa pode enviar', () => {
    const r = run('prata corolla bra2e19 vidro aberto', 'member')
    expect(r).toMatchObject({
      text: 'COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO',
      kind: 'vehicle',
      plate: 'BRA-2E19',
      canSend: true,
      duplicate: null,
      by: 'local',
    })
  })

  it('membro sem placa é recusado e diz o que falta', () => {
    const r = run('gol branco farol aceso', 'member')
    expect(r.kind).toBe('reject')
    expect(r.missing).toEqual(['placa'])
    expect(r.canSend).toBe(false)
    expect(r.text).toBe('')
  })

  it('membro com a placa na fila: repetido, não envia', () => {
    const r = run('gol branco ffo4745', 'member', queued)
    expect(r.duplicate).toMatchObject({ where: 'queue', label: 'GOL BRANCO' })
    expect(r.canSend).toBe(false)
  })

  it('admin com repetido: só avisado, pode enviar', () => {
    const r = run('gol branco ffo4745', 'admin', queued)
    expect(r.duplicate?.where).toBe('queue')
    expect(r.canSend).toBe(true)
  })

  it('admin sem placa: aviso livre', () => {
    const r = run('pais do pedro chegou', 'admin')
    expect(r).toMatchObject({ kind: 'free', canSend: true, text: 'PAIS DO PEDRO CHEGOU', plate: null, missing: [] })
  })

  it('admin sem nada para mostrar não envia', () => {
    const r = run('#', 'admin')
    expect(r.kind).toBe('reject')
    expect(r.canSend).toBe(false)
  })

  it('recusado pela IA nunca envia', () => {
    const r = evaluatePreview({ alert: parseAlert('x'), by: 'gemini', refused: true }, 'admin', null, NOW)
    expect(r).toMatchObject({ refused: true, canSend: false, text: '' })
  })

  it('texto final de membro acima de 200 letras: grande demais', () => {
    const r = run(`gol branco abc1234 ${'farol aceso '.repeat(20)}`, 'member')
    expect(r.tooLong).toBe(true)
    expect(r.canSend).toBe(false)
    expect(r.length).toBeGreaterThan(200)
  })

  it('o limite não vale para admin', () => {
    const r = run(`gol branco abc1234 ${'farol aceso '.repeat(30)}`, 'admin')
    expect(r.tooLong).toBe(false)
    expect(r.canSend).toBe(true)
  })

  it('o repetido muda com o /live sem nova correção', () => {
    const c = local('gol branco ffo4745')
    expect(evaluatePreview(c, 'member', null, NOW).canSend).toBe(true)
    expect(evaluatePreview(c, 'member', queued, NOW).canSend).toBe(false)
  })
})

describe('texto grande demais antes da correção', () => {
  it('membro: acima de 200 letras formatadas', () => {
    expect(isRawTooLong('a'.repeat(201), 'member')).toBe(true)
    expect(isRawTooLong('a'.repeat(200), 'member')).toBe(false)
    expect(isRawTooLong('a'.repeat(250), 'admin')).toBe(false)
  })

  it('tooLongResult não pode enviar', () => {
    expect(tooLongResult('a'.repeat(250))).toMatchObject({ tooLong: true, canSend: false, length: 250 })
  })
})
