import { describe, expect, it } from 'vitest'
import { userRecord } from '../test/fakes'
import { isNewMember, leavesAnotherAdmin, type Member, NEW_MEMBER_MS, sortMembers } from './members'

const NOW = 1_800_000_000_000
const m = (id: string, over: Partial<Member> = {}): Member => ({ id, ...userRecord(), ...over })

describe('sortMembers', () => {
  it('último uso decrescente; sem lastSeenAt vale a criação; não altera a lista original', () => {
    const list = [m('a', { lastSeenAt: 10 }), m('b', { lastSeenAt: 30 }), m('c', { lastSeenAt: undefined, createdAt: 20 })]
    expect(sortMembers(list).map((x) => x.id)).toEqual(['b', 'c', 'a'])
    expect(list.map((x) => x.id)).toEqual(['a', 'b', 'c'])
  })
})

describe('isNewMember', () => {
  it('menos de 7 dias é novo; 7 dias exatos não', () => {
    expect(isNewMember(m('a', { createdAt: NOW - 2 * 86_400_000 }), NOW)).toBe(true)
    expect(isNewMember(m('a', { createdAt: NOW - NEW_MEMBER_MS + 1 }), NOW)).toBe(true)
    expect(isNewMember(m('a', { createdAt: NOW - NEW_MEMBER_MS }), NOW)).toBe(false)
    expect(isNewMember(m('a', { createdAt: NOW - 10 * 86_400_000 }), NOW)).toBe(false)
  })
})

describe('leavesAnotherAdmin', () => {
  const a = m('a', { role: 'admin' })
  const b = m('b', { role: 'admin' })
  it('com outro admin ativo pode', () => expect(leavesAnotherAdmin([a, b], a)).toBe(true))
  it('o único admin não pode', () => expect(leavesAnotherAdmin([a, m('x')], a)).toBe(false))
  it('outro admin removido não conta', () => expect(leavesAnotherAdmin([a, m('b', { role: 'admin', blocked: true })], a)).toBe(false))
})
