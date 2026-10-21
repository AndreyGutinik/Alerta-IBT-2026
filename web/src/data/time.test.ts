import { describe, expect, it } from 'vitest'
import { formatDayTime, formatHm } from './time'

describe('time', () => {
  it('formatHm: HH:mm de 24 h', () => {
    expect(formatHm(new Date(2026, 9, 19, 19, 4).getTime())).toBe('19:04')
    expect(formatHm(new Date(2026, 9, 19, 0, 0).getTime())).toBe('00:00')
  })

  it('formatDayTime: dd/MM HH:mm', () => {
    expect(formatDayTime(new Date(2026, 9, 19, 9, 5).getTime())).toBe('19/10 09:05')
    expect(formatDayTime(new Date(2026, 0, 3, 23, 59).getTime())).toBe('03/01 23:59')
  })
})
