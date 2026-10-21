import { describe, expect, it } from 'vitest'
import { authErrorMessage } from './authErrors'

describe('authErrorMessage', () => {
  it.each([
    ['auth/popup-closed-by-user', 'Login cancelado.'],
    ['auth/cancelled-popup-request', 'Login cancelado.'],
    ['auth/network-request-failed', 'Sem conexão com a internet.'],
    ['auth/unauthorized-domain', 'Este endereço não está autorizado para login com Google.'],
    ['auth/too-many-requests', 'Muitas tentativas. Espere alguns minutos e tente de novo.'],
    ['auth/x', 'Não deu certo. Tente de novo. (auth/x)'],
  ])('%s', (code, message) => {
    expect(authErrorMessage({ code })).toBe(message)
  })

  it('erro sem código', () => {
    expect(authErrorMessage(new Error('boom'))).toBe('Não deu certo. Tente de novo.')
    expect(authErrorMessage(null)).toBe('Não deu certo. Tente de novo.')
  })
})
