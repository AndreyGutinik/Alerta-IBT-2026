import { LogIn } from 'lucide-react'
import { useState } from 'react'
import logo from '../../../assets/icons/logo.svg'
import { authErrorMessage } from '../session/authErrors'
import { useSession } from '../session/SessionProvider'

export function Login() {
  const { signIn } = useSession()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  async function handleClick() {
    setBusy(true)
    setError('')
    try {
      await signIn()
    } catch (e) {
      setError(authErrorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="splash">
      <div className="cd splash-card">
        <img className="logo" src={logo} alt="" width={72} height={72} />
        <h1>IBT Controller</h1>
        <p className="sub">Avisos para o telão</p>
        <div className="bar-demo" aria-hidden="true">
          <span>IBT CONTROLLER &nbsp;|&nbsp; AVISOS &nbsp;|&nbsp; TELÃO</span>
        </div>
        <button type="button" className="gbtn" onClick={handleClick} disabled={busy}>
          <LogIn aria-hidden="true" />
          Entrar com Google
        </button>
        <p className="err-line" role="alert">
          {error}
        </p>
      </div>
    </main>
  )
}
