import { useCallback, useEffect, useRef, useState } from 'react'

/** Um recado curto que some sozinho (1800 ms). `show('')` limpa na hora. */
export function useToast(): { message: string; show(msg: string, ms?: number): void } {
  const [message, setMessage] = useState('')
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const show = useCallback((msg: string, ms = 1800) => {
    clearTimeout(timer.current)
    setMessage(msg)
    if (msg) timer.current = setTimeout(() => setMessage(''), ms)
  }, [])

  useEffect(() => () => clearTimeout(timer.current), [])
  return { message, show }
}
