import { type FirebaseApp, initializeApp } from 'firebase/app'
import { connectDatabaseEmulator, type Database, getDatabase } from 'firebase/database'
import { type AppConfig, resolveAuthDomain } from './config'

export const EMULATOR_HOST = '127.0.0.1'
export const AUTH_EMULATOR_URL = `http://${EMULATOR_HOST}:9099`
export const DATABASE_EMULATOR_PORT = 9000

/** Liga o app do Firebase e o banco (o Auth e a IA são carregados à parte, só quando precisam). */
export function initFirebase(cfg: AppConfig, hostname = globalThis.location?.hostname ?? ''): { app: FirebaseApp; db: Database } {
  const app = initializeApp({
    ...cfg.firebase,
    authDomain: resolveAuthDomain(cfg.firebase.authDomain ?? '', hostname),
  })
  const db = getDatabase(app)
  if (cfg.useEmulators) connectDatabaseEmulator(db, EMULATOR_HOST, DATABASE_EMULATOR_PORT)
  return { app, db }
}
