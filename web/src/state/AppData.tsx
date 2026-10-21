import { createContext, type ReactNode, useContext, useEffect, useState } from 'react'
import type { AiCorrector } from '../correction/types'
import type { Backend } from '../data/backend'
import type { LiveState } from '../data/types'

interface AppData {
  backend: Backend
  corrector: AiCorrector | null
  live: LiveState | null
  connected: boolean
}

const AppDataContext = createContext<AppData | null>(null)

/** Dados compartilhados das telas. Só é montado com sessão ativa: assina /live e a conexão enquanto estiver na tela. */
export function AppDataProvider({
  backend,
  corrector,
  children,
}: {
  backend: Backend
  corrector: AiCorrector | null
  children: ReactNode
}) {
  const [live, setLive] = useState<LiveState | null>(null)
  const [connected, setConnected] = useState(false)

  useEffect(() => backend.watchLive(setLive), [backend])
  useEffect(() => backend.watchConnected(setConnected), [backend])

  return <AppDataContext.Provider value={{ backend, corrector, live, connected }}>{children}</AppDataContext.Provider>
}

function useAppData(): AppData {
  const data = useContext(AppDataContext)
  if (!data) throw new Error('Fora do AppDataProvider')
  return data
}

export const useBackend = (): Backend => useAppData().backend
export const useCorrector = (): AiCorrector | null => useAppData().corrector
export const useLive = (): LiveState | null => useAppData().live
export const useConnected = (): boolean => useAppData().connected

/** A hora do servidor, atualizada a cada `tickMs` (1 s): é ela que decide "PC offline" e "já passou há N min". */
export function useServerNow(tickMs = 1000): number {
  const backend = useBackend()
  const [now, setNow] = useState(() => backend.serverNow())
  useEffect(() => {
    setNow(backend.serverNow())
    const id = setInterval(() => setNow(backend.serverNow()), tickMs)
    return () => clearInterval(id)
  }, [backend, tickMs])
  return now
}
