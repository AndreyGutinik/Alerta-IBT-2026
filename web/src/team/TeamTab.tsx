import { LogOut } from 'lucide-react'
import { useSession } from '../session/SessionProvider'

/** Placeholder: a Task 11 troca pela lista da equipe. Por enquanto só dá ao admin o caminho do Sair. */
export function TeamTab() {
  const { signOut } = useSession()
  return (
    <div className="cd">
      <button type="button" className="gbtn" onClick={() => void signOut()}>
        <LogOut aria-hidden="true" />
        Sair
      </button>
    </div>
  )
}
