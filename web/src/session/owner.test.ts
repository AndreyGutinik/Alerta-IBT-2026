import { describe, expect, it } from 'vitest'
import { isOwnerEmail, OWNER_EMAIL } from './owner'

describe('isOwnerEmail', () => {
  it('reconhece o e-mail do mantenedor sem diferenciar maiúsculas nem espaços nas pontas', () => {
    expect(isOwnerEmail(OWNER_EMAIL)).toBe(true)
    expect(isOwnerEmail('  GutnikAndrey01@ProtonMail.com ')).toBe(true)
  })

  it('outros e-mails, vazio e ausente não são', () => {
    for (const other of ['gutnikandrey01@protonmail.com.br', 'x.gutnikandrey01@protonmail.com', 'gutnikandrey01@gmail.com', '', null, undefined]) {
      expect(isOwnerEmail(other)).toBe(false)
    }
  })
})
