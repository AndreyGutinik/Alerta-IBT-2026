import { Activity, Send, Users } from 'lucide-react'
import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { headerPill } from '../data/live'
import { SendTab } from '../send/SendTab'
import { useActiveSession } from '../session/SessionProvider'
import { useConnected, useLive, useServerNow } from '../state/AppData'
import { Avatar } from './Avatar'
import { Pill } from './Pill'

// Status e Equipe só são baixadas quando a pessoa abre a aba.
const StatusTab = lazy(() => import('../status/StatusTab').then((m) => ({ default: m.StatusTab })))
const TeamTab = lazy(() => import('../team/TeamTab').then((m) => ({ default: m.TeamTab })))

type Tab = 'send' | 'status' | 'team'

const TITLES: Record<Tab, string> = { send: 'Enviar', status: 'Agora', team: 'Equipe' }

/** A casca: cabeçalho (título, pílula do telão, avatar), a aba aberta e a barra de abas. */
export function Shell() {
  const { user, record, role, signOut } = useActiveSession()
  const live = useLive()
  const connected = useConnected()
  const serverNow = useServerNow()
  const [tab, setTab] = useState<Tab>('send')
  const [menuOpen, setMenuOpen] = useState(false)
  const menuRef = useRef<HTMLDivElement>(null)
  const admin = role === 'admin'

  // Rebaixado no meio do uso: a aba Equipe some e a pessoa volta para Enviar.
  const current: Tab = tab === 'team' && !admin ? 'send' : tab

  useEffect(() => {
    if (!menuOpen) return undefined
    const onDown = (e: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setMenuOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setMenuOpen(false)
    }
    document.addEventListener('pointerdown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [menuOpen])

  const pill = headerPill({ connected, live, serverNow })
  const tabs: { id: Tab; label: string; icon: typeof Send }[] = [
    { id: 'send', label: 'Enviar', icon: Send },
    { id: 'status', label: 'Status', icon: Activity },
    ...(admin ? [{ id: 'team' as const, label: 'Equipe', icon: Users }] : []),
  ]

  return (
    <div className="app">
      <header className="ab">
        <h1>{TITLES[current]}</h1>
        <div className="right">
          <Pill tone={pill.tone} label={pill.label} />
          <div className="menu-wrap" ref={menuRef}>
            <Avatar
              name={record.name || user.name}
              admin={admin}
              label={admin ? 'Equipe' : 'Conta'}
              expanded={admin ? undefined : menuOpen}
              onClick={admin ? () => setTab('team') : () => setMenuOpen((open) => !open)}
            />
            {menuOpen && !admin && (
              <div className="menu" role="menu">
                <button
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setMenuOpen(false)
                    void signOut()
                  }}
                >
                  Sair
                </button>
              </div>
            )}
          </div>
        </div>
      </header>

      <div className="ct" role="tabpanel">
        <div className="pane" hidden={current !== 'send'}>
          <SendTab />
        </div>
        {current !== 'send' && (
          <Suspense fallback={<div className="pane" aria-busy="true" />}>
            <div className="pane">{current === 'status' ? <StatusTab /> : <TeamTab />}</div>
          </Suspense>
        )}
      </div>

      <nav className="tb" role="tablist" aria-label="Abas">
        {tabs.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={current === id}
            className={current === id ? 't on' : 't'}
            onClick={() => setTab(id)}
          >
            <Icon aria-hidden="true" />
            {label}
          </button>
        ))}
      </nav>
    </div>
  )
}
