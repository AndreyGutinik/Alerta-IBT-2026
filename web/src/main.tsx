import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createLazyAuth } from './firebase/auth'
import { FirebaseBackend } from './firebase/backend'
import { readFirebaseConfig } from './firebase/config'
import { initFirebase } from './firebase/init'
import './styles/fonts.css'
import './styles/tokens.css'
import './styles/base.css'
import './styles/app.css'
import { ConfigMissing } from './ui/ConfigMissing'
import { Root } from './ui/Root'
import type { AiCorrector } from './correction/types'

function render(node: React.ReactNode) {
  createRoot(document.getElementById('root')!).render(<StrictMode>{node}</StrictMode>)
}

const result = readFirebaseConfig(import.meta.env)

if (!result.ok) {
  render(<ConfigMissing missing={result.missing} />)
} else {
  const { config } = result
  const { app, db } = initFirebase(config)
  const backend = new FirebaseBackend(db)
  const auth = createLazyAuth(app, { useEmulators: config.useEmulators })
  // A IA (Firebase AI Logic + App Check) só é baixada e ligada com a chave do reCAPTCHA e fora do emulador;
  // sem ela, ou se falhar, a prévia usa só o parser local.
  const recaptchaSiteKey = config.recaptchaSiteKey
  const corrector: Promise<AiCorrector | null> =
    recaptchaSiteKey && !config.useEmulators
      ? import('./correction/firebaseAi').then((m) =>
          m.createFirebaseAiCorrector(app, { model: config.aiModel, recaptchaSiteKey }),
        )
      : Promise.resolve(null)
  render(<Root auth={auth} backend={backend} corrector={corrector} />)
}
