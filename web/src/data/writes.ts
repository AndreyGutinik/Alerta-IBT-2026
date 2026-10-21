import { increment, serverTimestamp } from 'firebase/database'
import type { RequestPreview, SendInput, SessionUser } from './types'

/** O mesmo limite das regras para `name`. */
export const MAX_NAME = 80

/** Corta em `max` unidades UTF-16 sem deixar meio emoji (metade de par substituto) na ponta. */
function clip(text: string, max: number): string {
  if (text.length <= max) return text
  const lastKept = text.charCodeAt(max - 1)
  return text.slice(0, lastKept >= 0xd800 && lastKept <= 0xdbff ? max - 1 : max)
}

function displayName(name: string, email: string): string {
  const chosen = name.trim() || email.trim() || 'Sem nome'
  return clip(chosen, MAX_NAME)
}

/** O que muda no registro de quem já existe a cada entrada: o nome (do Google) e o último uso (hora do servidor). */
export function buildSeenUpdate(u: SessionUser): Record<string, unknown> {
  return { name: displayName(u.name, u.email), lastSeenAt: serverTimestamp() }
}

/** Registro novo de `/users/{uid}`: sempre `member` e não bloqueado (as regras só aceitam isso). */
export function buildUserRecord(u: SessionUser): Record<string, unknown> {
  return {
    name: displayName(u.name, u.email),
    email: u.email,
    role: 'member',
    blocked: false,
    createdAt: serverTimestamp(),
    lastSeenAt: serverTimestamp(),
  }
}

function buildPreview(preview: RequestPreview): Record<string, unknown> {
  const out: Record<string, unknown> = { text: preview.text, kind: preview.kind }
  if (preview.plate) out.plate = preview.plate
  out.by = preview.by
  return out
}

/**
 * Atualização de vários caminhos, a partir da raiz, que cria o pedido e marca o envio da pessoa
 * (`lastSentAt`, `lastRequestId` e `sentCount`) de uma vez: é o que as regras exigem para o limite
 * de 20 s. O pedido vai sem `status` (= pendente) e `raw` já deve vir limpo e com até 300 caracteres.
 */
export function buildSendUpdate(id: string, input: SendInput): Record<string, unknown> {
  const request: Record<string, unknown> = {
    uid: input.uid,
    name: displayName(input.name, ''),
    raw: input.raw,
    urgent: input.urgent,
    createdAt: serverTimestamp(),
    cancel: false,
  }
  if (input.preview) request.preview = buildPreview(input.preview)

  return {
    [`requests/${id}`]: request,
    [`users/${input.uid}/lastSentAt`]: serverTimestamp(),
    [`users/${input.uid}/lastRequestId`]: id,
    [`users/${input.uid}/sentCount`]: increment(1),
  }
}
