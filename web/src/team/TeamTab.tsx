import { LogOut, ShieldCheck, ShieldOff, UserCheck, UserX } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import type { Role } from '../alerts/types'
import { formatDayTime } from '../data/time'
import { useActiveSession } from '../session/SessionProvider'
import { useBackend, useServerNow } from '../state/AppData'
import { initialOf } from '../ui/Avatar'
import { Toast } from '../ui/Toast'
import { useToast } from '../ui/useToast'
import { isNewMember, leavesAnotherAdmin, type Member, sortMembers } from './members'

export const MSG_TEAM_FAILED = 'Não deu certo. Tente de novo.'
const MSG_LAST_ADMIN = 'É o único admin: não pode ser removido.'

function count(n: number): string {
  return n === 1 ? '1 aviso' : `${n} avisos`
}

/** Aba Equipe (só admin): quem já entrou, promover/rebaixar, remover (bloquear) e restaurar. "Sair" fica no fim. */
export function TeamTab() {
  const backend = useBackend()
  const { user, signOut } = useActiveSession()
  const serverNow = useServerNow(60_000)
  const { message, show } = useToast()
  const [list, setList] = useState<Member[] | null>(null)
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set())

  useEffect(() => backend.watchUsers(setList), [backend])

  const run = useCallback(
    async (uid: string, action: () => Promise<void>) => {
      setBusy((prev) => new Set(prev).add(uid))
      try {
        await action()
      } catch {
        show(MSG_TEAM_FAILED, 4000)
      } finally {
        setBusy((prev) => {
          const next = new Set(prev)
          next.delete(uid)
          return next
        })
      }
    },
    [show],
  )

  const members = list === null ? [] : sortMembers(list)

  return (
    <>
      <section aria-labelledby="team-title">
        <h2 className="lb" id="team-title">
          {list === null ? 'Quem já entrou' : `Quem já entrou (${members.length})`}
        </h2>
        {list === null ? (
          <p className="empty" aria-busy="true">
            Carregando…
          </p>
        ) : members.length === 0 ? (
          <p className="empty">Ninguém entrou ainda.</p>
        ) : (
          <ul className="list">
            {members.map((m) => {
              const self = m.id === user.uid
              const admin = m.role === 'admin'
              const working = busy.has(m.id)
              // O único admin ativo não pode perder o cargo nem ser removido.
              const lastAdmin = admin && !m.blocked && !leavesAnotherAdmin(members, m)
              const nextRole: Role = admin ? 'member' : 'admin'
              return (
                <li key={m.id} className="mem">
                  <div className="mem-top">
                    <span className={admin ? 'avatar adm' : 'avatar'} aria-hidden="true">
                      {initialOf(m.name || m.email)}
                    </span>
                    <div className="mem-info">
                      <div className="mem-name">
                        <span className="mem-n">{m.name}</span>
                        {self && <span className="bdg you">você</span>}
                        {admin && <span className="bdg adm">admin</span>}
                        {m.blocked && <span className="bdg off">removido</span>}
                        {isNewMember(m, serverNow) && <span className="bdg new">novo</span>}
                      </div>
                      <div className="mem-mail">{m.email}</div>
                      <div className="mem-meta">
                        {m.lastSeenAt === undefined ? 'Último uso: nunca' : `Último uso: ${formatDayTime(m.lastSeenAt)}`}
                        {' · '}
                        {count(m.sentCount ?? 0)}
                      </div>
                    </div>
                  </div>
                  {!self && (
                    <div className="mem-actions">
                      {(admin || !m.blocked) && (
                        <button
                          type="button"
                          className="mbtn"
                          disabled={working || (admin && lastAdmin)}
                          title={admin && lastAdmin ? MSG_LAST_ADMIN : undefined}
                          onClick={() => void run(m.id, () => backend.setRole(m.id, nextRole))}
                        >
                          {admin ? <ShieldOff aria-hidden="true" /> : <ShieldCheck aria-hidden="true" />}
                          {admin ? 'Tirar admin' : 'Tornar admin'}
                        </button>
                      )}
                      <button
                        type="button"
                        className={m.blocked ? 'mbtn' : 'mbtn danger'}
                        disabled={working || (!m.blocked && lastAdmin)}
                        title={!m.blocked && lastAdmin ? MSG_LAST_ADMIN : undefined}
                        onClick={() => void run(m.id, () => backend.setBlocked(m.id, !m.blocked))}
                      >
                        {m.blocked ? <UserCheck aria-hidden="true" /> : <UserX aria-hidden="true" />}
                        {m.blocked ? 'Restaurar' : 'Remover'}
                      </button>
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
        )}
        <Toast message={message} />
      </section>
      <button type="button" className="gbtn out" onClick={() => void signOut()}>
        <LogOut aria-hidden="true" />
        Sair
      </button>
    </>
  )
}
