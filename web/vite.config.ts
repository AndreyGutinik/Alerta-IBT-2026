import { resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import { searchForWorkspaceRoot } from 'vite'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  server: {
    fs: {
      // As fontes Marble (../assets/fonts) e os casos compartilhados com o app do PC
      // (../shared/parser-cases.json) ficam fora da pasta web/.
      allow: [
        searchForWorkspaceRoot(process.cwd()),
        resolve(import.meta.dirname, '../assets'),
        resolve(import.meta.dirname, '../shared'),
      ],
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
