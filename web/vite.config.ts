import react from '@vitejs/plugin-react'
import type { ProxyOptions } from 'vite'
import { defineConfig } from 'vitest/config'

// In development the page comes from Vite (http://localhost:5173, hot reload) and everything else from a
// running Host. In normal use there is no Vite: the Host serves the built files itself (same origin).
const host = process.env.ITRADE_HOST ?? 'https://127.0.0.1:7120'

const toHost: ProxyOptions = {
  target: host,
  // The Host's certificate is the local development one; the connection never leaves this machine.
  secure: false,
}

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': toHost,
      '/health': toHost,
      '/openapi': toHost,
      '/hubs': {
        ...toHost,
        ws: true,
        // The hub accepts its own origin only: present the proxied connection as the Host's own page.
        changeOrigin: true,
        headers: { Origin: host },
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
    unstubGlobals: true,
  },
})
