import { ShieldOff } from 'lucide-react'
import { useSession } from '../session/SessionProvider'

export function Blocked() {
  const { signOut } = useSession()
  return (
    <main className="splash">
      <ShieldOff className="big-icon" aria-hidden="true" />
      <h1>Seu acesso foi removido</h1>
      <p className="sub">Se isso foi um engano, fale com a equipe do som e luz.</p>
      <button type="button" className="gbtn" onClick={() => void signOut()}>
        Sair
      </button>
    </main>
  )
}
