/**
 * O mantenedor do app: sempre administrador e nunca bloqueado, rebaixado nem removido, nem por outro admin.
 * É o único lugar do site com este e-mail. O PC tem a cópia dele (SiteContract.ProtectedAdmins) e o
 * database.rules.json repete o e-mail em duas regras (role e blocked), porque regras não têm constantes: o teste
 * tests/emulator/owner.test.ts falha se as cópias divergirem.
 */
export const OWNER_EMAIL = 'gutnikandrey01@protonmail.com'

/** O e-mail é o do mantenedor? Sem diferenciar maiúsculas e sem espaços nas pontas, como no PC e nas regras. */
export function isOwnerEmail(email: string | null | undefined): boolean {
  return typeof email === 'string' && email.trim().toLowerCase() === OWNER_EMAIL
}
