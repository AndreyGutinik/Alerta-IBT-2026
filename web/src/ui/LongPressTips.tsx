import { useEffect, useState } from 'react'

/** Quanto tempo segurar (ms) para a dica aparecer, e quanto ela fica na tela. */
export const LONG_PRESS_MS = 450
export const TIP_SHOW_MS = 1800
/** Se o dedo andar mais que isto, é rolagem, não toque longo. */
const MOVE_LIMIT_PX = 10

interface Tip {
  text: string
  x: number
  y: number
}

/**
 * No celular não existe "passar o mouse": segurar o dedo uns 0,5 s sobre um ícone com `title` mostra o mesmo texto
 * numa dica pequena. O toque que termina a pressão não aciona o botão (senão segurar o X cancelaria o aviso).
 * No computador o `title` nativo já faz o trabalho; aqui só conta toque e caneta.
 */
export function LongPressTips() {
  const [tip, setTip] = useState<Tip | null>(null)

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    let hideTimer: ReturnType<typeof setTimeout> | undefined
    let start: { x: number; y: number } | null = null
    let shown = false

    const cancel = () => {
      clearTimeout(timer)
      timer = undefined
      start = null
    }

    const onDown = (e: PointerEvent) => {
      shown = false
      if (e.pointerType === 'mouse') return
      const el = (e.target as Element | null)?.closest?.('[title]')
      const text = el?.getAttribute('title')
      if (!el || !text) return
      start = { x: e.clientX, y: e.clientY }
      clearTimeout(timer)
      timer = setTimeout(() => {
        const box = el.getBoundingClientRect()
        shown = true
        start = null
        setTip({ text, x: Math.min(Math.max(box.left + box.width / 2, 120), window.innerWidth - 120), y: box.top - 8 })
        clearTimeout(hideTimer)
        hideTimer = setTimeout(() => setTip(null), TIP_SHOW_MS)
      }, LONG_PRESS_MS)
    }

    const onMove = (e: PointerEvent) => {
      if (!start) return
      if (Math.hypot(e.clientX - start.x, e.clientY - start.y) > MOVE_LIMIT_PX) cancel()
    }

    // O clique que vem depois de uma pressão longa com dica não vale.
    const onClick = (e: MouseEvent) => {
      if (!shown) return
      shown = false
      e.preventDefault()
      e.stopPropagation()
    }

    const onContextMenu = (e: Event) => {
      if (timer !== undefined || shown) e.preventDefault()
    }

    document.addEventListener('pointerdown', onDown)
    document.addEventListener('pointermove', onMove)
    document.addEventListener('pointerup', cancel)
    document.addEventListener('pointercancel', cancel)
    document.addEventListener('click', onClick, true)
    document.addEventListener('contextmenu', onContextMenu)
    return () => {
      cancel()
      clearTimeout(hideTimer)
      document.removeEventListener('pointerdown', onDown)
      document.removeEventListener('pointermove', onMove)
      document.removeEventListener('pointerup', cancel)
      document.removeEventListener('pointercancel', cancel)
      document.removeEventListener('click', onClick, true)
      document.removeEventListener('contextmenu', onContextMenu)
    }
  }, [])

  if (!tip) return null
  return (
    <div className="press-tip" role="tooltip" style={{ left: tip.x, top: tip.y }}>
      {tip.text}
    </div>
  )
}
