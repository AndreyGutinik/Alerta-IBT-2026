import { resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import { searchForWorkspaceRoot } from 'vite'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  server: {
    fs: {
      // As fontes Marble ficam em ../assets/fonts, fora da pasta web/.
      allow: [searchForWorkspaceRoot(process.cwd()), resolve(import.meta.dirname, '../assets')],
    },
  },
  test: {
    projects: [
      {
        extends: true,
        test: {
          name: 'unit',
          environment: 'jsdom',
          include: ['src/**/*.test.{ts,tsx}'],
          setupFiles: ['src/test/setup.ts'],
        },
      },
    ],
  },
})
