import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
      '/health': { target: 'http://localhost:5000', changeOrigin: true },
      '/openapi': { target: 'http://localhost:5000', changeOrigin: true },
      // PHASE4_DESIGN.md §8.1: the A-064 guest bearer-link exchange lives outside /api/v1.
      // Regex key: a bare '/g' prefix-matches /guest/* SPA routes and would proxy them too.
      '^/g/': { target: 'http://localhost:5000', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/setupTests.ts'],
    // Full process isolation per test file. The default `threads` pool does
    // not fully isolate `vi.useFakeTimers`' global `Date` patch across
    // concurrently-scheduled files under load — WeekGridPage.test.tsx and
    // SessionCreatePage.test.tsx (the only two files faking `Date`) could
    // observe each other's real/fake clock mid-run, intermittently failing
    // date-anchored fixtures. `forks` runs each file in its own OS process,
    // which removes that shared-global surface entirely.
    pool: 'forks',
  },
})
