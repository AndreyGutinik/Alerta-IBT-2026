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
      <img className="logo" src={logo} alt="" width={72} height={72} />
      <h1>Alerta IBT</h1>
      <p className="sub">Avisos para o telão</p>
      <button type="button" className="gbtn" onClick={handleClick} disabled={busy}>
        Entrar com Google
      </button>
      <p className="err-line" role="alert">
        {error}
      </p>
    </main>
  )
}
