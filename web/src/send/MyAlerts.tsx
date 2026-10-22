import { Ban, Check, Clock, Hourglass, type LucideIcon, MonitorPlay, X } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import type { RequestBadge } from '../data/requestView'
import { requestBadge, requestText } from '../data/requestView'
import type { RequestRecord, WithId } from '../data/types'
import { useBackend, useLive } from '../state/AppData'
import { Toast } from '../ui/Toast'
import { useToast } from '../ui/useToast'
import { PassProgress } from './PassProgress'

/** Quantos avisos aparecem (os mais novos). */
export const MY_ALERTS_LIMIT = 10

const ICONS: Record<RequestBadge['icon'], LucideIcon> = {
  clock: Clock,
  'monitor-play': MonitorPlay,
  check: Check,
  x: X,
  ban: Ban,
  hourglass: Hourglass,
}

/** "Seus avisos": o que a pessoa mandou e em que pé está, escrito pelo PC. Cancela os que ainda não saíram. */
export function MyAlerts({ uid }: { uid: string }) {
  const backend = useBackend()
  const live = useLive()
  const { message, show } = useToast()
  const [list, setList] = useState<WithId<RequestRecord>[]>([])
  const [cancelling, setCancelling] = useState<ReadonlySet<string>>(new Set())

  useEffect(() => backend.watchMyRequests(uid, setList), [backend, uid])

  const cancel = useCallback(
    async (id: string) => {
      setCancelling((prev) => new Set(prev).add(id))
      try {
        await backend.cancel(id)
      } catch {
        show('Não deu para cancelar. Tente de novo.', 4000)
        setCancelling((prev) => {
          const next = new Set(prev)
          next.delete(id)
          return next
        })
      }
    },
    [backend, show],
  )

  const items = [...list].sort((a, b) => b.createdAt - a.createdAt).slice(0, MY_ALERTS_LIMIT)

  return (
    <section aria-labelledby="my-alerts-title">
      <h2 className="lb" id="my-alerts-title">
        Seus avisos
      </h2>
      {items.length === 0 ? (
        <p className="empty">Nenhum aviso ainda.</p>
      ) : (
        <ul className="list">
          {items.map((r) => {
            const badge = requestBadge(r, live)
            const Icon = ICONS[badge.icon]
            const text = requestText(r)
            return (
              <li key={r.id} className={badge.struck ? 'it cx' : 'it'}>
                <span className="t" title={text}>
                  {text}
                </span>
                <span className={`st ${badge.tone}`}>
                  <Icon aria-hidden="true" />
                  {badge.label}
                </span>
                {badge.canCancel && (
                  <button
                    type="button"
                    className="xb"
                    title="Cancelar (o carro já saiu)"
                    aria-label="Cancelar aviso"
                    disabled={cancelling.has(r.id)}
                    onClick={() => void cancel(r.id)}
                  >
                    <X aria-hidden="true" />
                  </button>
                )}
                {live?.now?.id === r.id && <PassProgress now={live.now} bar={live.bar} />}
              </li>
            )
          })}
        </ul>
      )}
      <Toast message={message} />
    </section>
  )
}
