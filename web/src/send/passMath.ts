import { LED_WIDTH } from '../status/ticker'

/** Entrada e saída da barra no LED (a animação de 320 ms do PC, `BarTimeline`). */
export const SLIDE_MS = 320

/** Menor velocidade aceita no cálculo: evita dividir por zero se o PC publicar uma velocidade inválida. */
const MIN_SPEED = 1

/**
 * Quanto dura a passagem inteira no LED, igual à `BarTimeline` do PC: entra (320 ms), o texto anda da borda direita
 * até sair inteiro pela esquerda (`tela + texto` px na `speed` px/s) e sai (320 ms).
 */
export function passTotalMs(speed: number, textWidth: number, screenWidth = LED_WIDTH): number {
  const pxPerSecond = Math.max(MIN_SPEED, Number.isFinite(speed) ? speed : MIN_SPEED)
  const scrollMs = ((screenWidth + Math.max(0, textWidth)) / pxPerSecond) * 1000
  return SLIDE_MS * 2 + scrollMs
}

/** O quanto da passagem já foi, de 0 a 1, `elapsedMs` depois do `startedAt` do PC (hora do servidor). */
export function passProgress(elapsedMs: number, speed: number, textWidth: number, screenWidth = LED_WIDTH): number {
  return clampRatio(elapsedMs / passTotalMs(speed, textWidth, screenWidth))
}

function clampRatio(ratio: number): number {
  return Math.min(1, Math.max(0, ratio))
}

/** A duração exata publicada pelo PC só vale se for um número finito maior que zero. */
export function isExactDuration(durationMs: unknown): durationMs is number {
  return typeof durationMs === 'number' && Number.isFinite(durationMs) && durationMs > 0
}

/**
 * O andamento da passagem que está no LED. Com a duração exata do PC (`now.durationMs`) conta direto por ela e não
 * mede o texto; sem ela (PC antigo, valor inválido) estima por velocidade + largura do texto (`measureTextWidth`).
 */
export function nowProgress(
  elapsedMs: number,
  durationMs: number | undefined,
  speed: number,
  measureTextWidth: () => number,
): number {
  if (isExactDuration(durationMs)) return clampRatio(elapsedMs / durationMs)
  return passProgress(elapsedMs, speed, measureTextWidth())
}
