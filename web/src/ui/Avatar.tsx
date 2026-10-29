export function initialOf(name: string): string {
  const first = Array.from(name.trim())[0]
  return first ? first.toLocaleUpperCase('pt-BR') : '?'
}

/** A inicial da pessoa; o ponto amarelo marca o admin. É um botão (área de toque grande, o círculo é só o visual). */
export function Avatar({
  name,
  admin,
  label,
  expanded,
  onClick,
}: {
  name: string
  admin: boolean
  label: string
  expanded?: boolean
  onClick: () => void
}) {
  return (
    <button type="button" className="avatar-btn" aria-label={label} title={label} aria-expanded={expanded} onClick={onClick}>
      <span className={admin ? 'avatar adm' : 'avatar'} aria-hidden="true">
        {initialOf(name)}
      </span>
    </button>
  )
}
