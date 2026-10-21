import type { UserRecord, WithId } from '../data/types'

/** "novo" = entrou há menos de 7 dias (hora do servidor). */
export const NEW_MEMBER_MS = 7 * 86_400_000

export type Member = WithId<UserRecord>

/** Último uso decrescente; quem nunca teve `lastSeenAt` conta pela data de criação. Estável para empates. */
export function sortMembers(list: readonly Member[]): Member[] {
  const seen = (m: Member) => m.lastSeenAt ?? m.createdAt
  return [...list].sort((a, b) => seen(b) - seen(a))
}

export function isNewMember(m: Member, serverNow: number): boolean {
  return serverNow - m.createdAt < NEW_MEMBER_MS
}

/**
 * Tirar o admin (ou remover) de `target` deixa algum admin ativo? Um admin removido não conta. A regra do
 * produto: nunca ficar sem admin. Como o admin não mexe na própria linha, na prática ele mesmo é o que sobra;
 * a conta vale também para uma lista desatualizada.
 */
export function leavesAnotherAdmin(list: readonly Member[], target: Member): boolean {
  return list.some((m) => m.id !== target.id && m.role === 'admin' && !m.blocked)
}
