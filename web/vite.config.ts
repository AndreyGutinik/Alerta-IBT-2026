import { resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import { searchForWorkspaceRoot } from 'vite'
import { VitePWA } from 'vite-plugin-pwa'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [
    react(),
    // PWA: o app abre do cache (casca do app offline). O service worker só guarda os arquivos do próprio
    // site (JS, CSS, fontes, ícones); não há cache de execução, então o /live e o tráfego do banco
    // (Realtime Database, Auth, IA) nunca passam por ele nem ficam guardados.
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.svg', 'apple-touch-icon.png'],
      manifest: {
        name: 'Alerta IBT',
        short_name: 'Alerta IBT',
        description: 'Avisos para o telão da IBT',
        lang: 'pt-BR',
        dir: 'ltr',
        theme_color: '#0a0a0c',
        background_color: '#0a0a0c',
        display: 'standalone',
        orientation: 'portrait',
        start_url: '/',
        scope: '/',
        icons: [
          { src: 'icon-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
          { src: 'icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
          { src: 'icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
          { src: 'favicon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,ttf,webmanifest}'],
        navigateFallback: 'index.html',
        // /__/auth/* (login do Firebase) e /__/firebase/* são do Hosting, nunca a casca do app.
        navigateFallbackDenylist: [/^\/__\//],
        cleanupOutdatedCaches: true,
        clientsClaim: true,
        skipWaiting: true,
      },
    }),
  ],
  build: {
    // Cada aba e cada SDK pesado (Auth, IA) vira um pedaço separado; o aviso de tamanho do Vite
    // fica no padrão (500 kB) para acusar qualquer pedaço que crescer demais.
    chunkSizeWarningLimit: 500,
  },
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
      {
        // Testes das regras do banco: precisam do emulador (npm run test:emu).
        extends: true,
        test: {
          name: 'emulator',
          environment: 'node',
          include: ['tests/emulator/**/*.test.ts'],
          fileParallelism: false,
          testTimeout: 20000,
          hookTimeout: 30000,
        },
      },
    ],
  },
})
