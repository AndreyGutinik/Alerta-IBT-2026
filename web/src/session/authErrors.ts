function codeOf(e: unknown): string {
  if (typeof e === 'object' && e !== null && 'code' in e) {
    const code = (e as { code: unknown }).code
    if (typeof code === 'string') return code
  }
  return ''
}

/** Mensagem em pt-BR para um erro do login com Google (códigos `auth/...` do Firebase). */
export function authErrorMessage(e: unknown): string {
  const code = codeOf(e)
  switch (code) {
    case 'auth/popup-closed-by-user':
    case 'auth/cancelled-popup-request':
      return 'Login cancelado.'
    case 'auth/network-request-failed':
      return 'Sem conexão com a internet.'
    case 'auth/unauthorized-domain':
      return 'Este endereço não está autorizado para login com Google.'
    case 'auth/too-many-requests':
      return 'Muitas tentativas. Espere alguns minutos e tente de novo.'
    default:
      return code ? `Não deu certo. Tente de novo. (${code})` : 'Não deu certo. Tente de novo.'
  }
}
