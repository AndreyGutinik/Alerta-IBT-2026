import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Sem `globals: true` o Testing Library não limpa o DOM sozinho entre os testes.
afterEach(() => {
  cleanup()
  // O app guarda o tutorial e o último registro da pessoa no aparelho: nada vaza de um teste para o outro.
  try {
    localStorage.clear()
  } catch {
    // sem armazenamento
  }
})
