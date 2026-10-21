import type { Tone } from '../data/live'

/** A pílula do cabeçalho: bolinha na cor do estado e o texto. */
export function Pill({ tone, label }: { tone: Tone; label: string }) {
  return (
    <span className="pill" role="status">
      <span className={`dot ${tone}`} aria-hidden="true" />
      {label}
    </span>
  )
}
