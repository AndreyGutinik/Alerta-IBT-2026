import type { LiveBar } from '../data/types'

/** A largura do LED da igreja em px (2560x768): todas as medidas do mini-telão usam essa régua. */
export const LED_WIDTH = 2560
export const LED_HEIGHT = 768

/**
 * Onde está o começo do texto da barra, em px do LED, `elapsedMs` depois de ele começar a passar.
 * Entra pela direita (`screenWidth`), anda `speed` px por segundo e, ao sair inteiro pela esquerda, volta
 * para a direita (laço).
 */
export function tickerX(elapsedMs: number, speed: number, textWidth: number, screenWidth = LED_WIDTH): number {
  const loop = screenWidth + Math.max(0, textWidth)
  const travelled = (Math.max(0, speed) * Math.max(0, elapsedMs)) / 1000
  return screenWidth - (travelled % loop)
}

/** Altura, tamanho da letra (px do LED; sem `fontSize` vale 64% da altura) e se a barra está no topo. */
export function barMetrics(bar: LiveBar): { height: number; fontSize: number; top: boolean } {
  return {
    height: bar.height,
    fontSize: bar.fontSize ?? Math.round(bar.height * 0.64),
    top: bar.position === 'top',
  }
}
