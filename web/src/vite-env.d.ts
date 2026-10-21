/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_FIREBASE_API_KEY?: string
  readonly VITE_FIREBASE_AUTH_DOMAIN?: string
  readonly VITE_FIREBASE_PROJECT_ID?: string
  readonly VITE_FIREBASE_DATABASE_URL?: string
  readonly VITE_FIREBASE_APP_ID?: string
  readonly VITE_RECAPTCHA_SITE_KEY?: string
  readonly VITE_AI_MODEL?: string
  readonly VITE_USE_EMULATORS?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
