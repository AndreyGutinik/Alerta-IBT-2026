const pad = (n: number): string => String(n).padStart(2, '0')

/** `HH:mm`, 24 h, no fuso do aparelho. */
export function formatHm(ms: number): string {
  const d = new Date(ms)
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** `dd/MM HH:mm`, 24 h, no fuso do aparelho. */
export function formatDayTime(ms: number): string {
  const d = new Date(ms)
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)} ${formatHm(ms)}`
}
