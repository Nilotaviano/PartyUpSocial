import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // D-138 / PHASE8_DESIGN.md §2 Q3 (ratified 2026-08-12), documented in docs/BROWSER_SUPPORT.md:
    // the compilation floor for the four supported families. esbuild targets are dependency-free
    // (no browserslist package, no @vitejs/plugin-legacy) and each name is a FLOOR, deliberately
    // at or below the current-minus-one release of its family rather than a claim about which
    // version is current today — every supported browser is newer than its floor, so raising a
    // floor can only break support, never widen it. Reviewed each phase; Edge tracks Chromium.
    target: ['chrome133', 'edge133', 'firefox135', 'safari18'],
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
      '/health': { target: 'http://localhost:5000', changeOrigin: true },
      '/openapi': { target: 'http://localhost:5000', changeOrigin: true },
      // PHASE4_DESIGN.md §8.1: the A-064 guest bearer-link exchange lives outside /api/v1.
      // Regex key: a bare '/g' prefix-matches /guest/* SPA routes and would proxy them too.
      '^/g/': { target: 'http://localhost:5000', changeOrigin: true },
      // AvatarProxyEndpoints.cs: '/a/{token}' serves external avatar images
      // outside /api/v1. Same rationale as '^/g/' above: a bare '/a' prefix
      // would also match SPA routes like /account-deletion, so this needs
      // the trailing-slash regex form.
      '^/a/': { target: 'http://localhost:5000', changeOrigin: true },
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
