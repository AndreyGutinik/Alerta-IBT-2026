import { LogOut, ShieldOff } from 'lucide-react'
import { useSession } from '../session/SessionProvider'

export function Blocked() {
  const { signOut } = useSession()
  return (
    <main className="splash">
      <div className="cd splash-card">
        <ShieldOff className="big-icon" aria-hidden="true" />
        <h1>Seu acesso foi removido</h1>
        <p className="sub">Se foi engano, fale com a equipe do som e luz.</p>
        <button type="button" className="gbtn" onClick={() => void signOut()}>
          <LogOut aria-hidden="true" />
          Sair
        </button>
      </div>
    </main>
  )
}
