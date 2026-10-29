import { Ban, Check, History, Hourglass, ListOrdered, type LucideIcon, MonitorPlay, Siren, X } from 'lucide-react'
import { useCallback, useState } from 'react'
import { isPcOnline, type Tone } from '../data/live'
import { formatHm } from '../data/time'
import type { LiveBar, LiveItem, LiveRecent, LiveSource } from '../data/types'
import { useActiveSession } from '../session/SessionProvider'
import { useBackend, useConnected, useLive, useServerNow } from '../state/AppData'
import { Toast } from '../ui/Toast'
import { useToast } from '../ui/useToast'
import { MiniLed } from './MiniLed'

/** Quantos "Recentes" aparecem (os mais novos). */
export const RECENT_LIMIT = 10

const DEFAULT_BAR: LiveBar = { position: 'top', height: 56, speed: 90 }
const SOURCE_LABEL: Record<LiveSource, string> = { telegram: 'Telegram', site: 'Site', pc: 'PC' }

const RECENT_BADGE: Record<
  LiveRecent['state'],
  { tone: 'ok' | 'no' | 'cx'; icon: LucideIcon; label: (r: LiveRecent) => string; struck: boolean }
> = {
  shown: { tone: 'ok', icon: Check, label: (r) => formatHm(r.at), struck: false },
  rejected: { tone: 'no', icon: X, label: () => 'recusado', struck: false },
  cancelled: { tone: 'cx', icon: Ban, label: () => 'cancelado', struck: true },
  expired: { tone: 'cx', icon: Hourglass, label: () => 'expirou', struck: true },
}

function StatePill({ tone, label }: { tone: Tone; label: string }) {
  return (
    <span className="pill">
      <span className={`dot ${tone}`} aria-hidden="true" />
      {label}
    </span>
  )
}

/**
 * A aba Status: o que está no telão agora (com o mini-telão), LED e barra, a fila e os recentes. Tudo vem de
 * `/live`. Sem heartbeat há mais de 90 s (hora do servidor) o PC é dado como offline e o aviso velho some.
 * A pílula "PC online" fica no cabeçalho (Shell), como no mockup.
 */
export function StatusTab() {
  const backend = useBackend()
  const { role } = useActiveSession()
  const live = useLive()
  const connected = useConnected()
  const serverNow = useServerNow()
  const { message, show } = useToast()
  const [cancelling, setCancelling] = useState<ReadonlySet<string>>(new Set())
  const admin = role === 'admin'

  const online = isPcOnline(live, serverNow)
  const barOn = live !== null && online && live.led && !live.paused
  const now = online && live ? live.now : null
  const shownNow = barOn ? now : null

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

  let line: string
  if (!live || !online) line = 'PC offline. Sem informação do telão agora.'
  else if (!live.led) line = 'LED desligado: a barra não está no telão.'
  else if (live.paused) line = 'Barra pausada (pregação).'
  else if (!now) line = 'Nenhum aviso no telão agora.'
  else line = now.text

  const queue: LiveItem[] = live?.queue ?? []
  const recent = [...(live?.recent ?? [])].sort((a, b) => b.at - a.at).slice(0, RECENT_LIMIT)
  const stale = !online || !connected ? ' stale' : ''

  return (
    <>
      <div className="cd live-card">
        <div className="lb">
          <span>
            <MonitorPlay aria-hidden="true" />
            No telão agora
          </span>
          {shownNow && <span className="src">{SOURCE_LABEL[shownNow.source]}</span>}
        </div>
        <MiniLed text={shownNow?.text ?? null} bar={live?.bar ?? DEFAULT_BAR} startedAt={shownNow?.startedAt} />
        <div className={shownNow ? 'msg' : 'msg idle'}>{line}</div>
        {!connected && online && <p className="note">Sem conexão: o que aparece aqui pode estar desatualizado.</p>}
        {live && online && (
          <div className="pills">
            <StatePill tone={live.led ? 'ok' : 'bad'} label={live.led ? 'LED ligado' : 'LED desligado'} />
            <StatePill tone={live.paused ? 'warn' : 'ok'} label={live.paused ? 'Barra pausada' : 'Barra ativa'} />
          </div>
        )}
      </div>

      <section aria-labelledby="queue-title" className={`cd block${stale}`}>
        <h2 className="lb" id="queue-title">
          <span>
            <ListOrdered aria-hidden="true" />
            {`Fila (${queue.length})`}
          </span>
        </h2>
        {queue.length === 0 ? (
          <p className="empty">
            <Check aria-hidden="true" />
            Fila vazia.
          </p>
        ) : (
          <ul className="list">
            {queue.map((item, i) => (
              <li key={`${item.id}-${i}`} className="it">
                <span className="src">{SOURCE_LABEL[item.source]}</span>
                <span className="t" title={item.text}>
                  {item.text}
                </span>
                <span className="st q">
                  {item.urgent && <Siren aria-label="Urgente" role="img" />}
                  {`${i + 1}º`}
                </span>
                {admin && item.source === 'site' && (
                  <button
                    type="button"
                    className="xb"
                    title="Cancelar aviso"
                    aria-label="Cancelar aviso"
                    disabled={cancelling.has(item.id)}
                    onClick={() => void cancel(item.id)}
                  >
                    <X aria-hidden="true" />
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="recent-title" className={`cd block${stale}`}>
        <h2 className="lb" id="recent-title">
          <span>
            <History aria-hidden="true" />
            Recentes
          </span>
        </h2>
        {recent.length === 0 ? (
          <p className="empty">
            <History aria-hidden="true" />
            Nada por enquanto.
          </p>
        ) : (
          <ul className="list">
            {recent.map((r, i) => {
              const badge = RECENT_BADGE[r.state]
              const Icon = badge.icon
              return (
                <li key={`${r.id}-${i}`} className={badge.struck ? 'it cx' : 'it'}>
                  <span className="t" title={r.text}>
                    {r.text}
                  </span>
                  <span className={`st ${badge.tone}`}>
                    <Icon aria-hidden="true" />
                    {badge.label(r)}
                  </span>
                </li>
              )
            })}
          </ul>
        )}
      </section>
      <Toast message={message} />
    </>
  )
}
