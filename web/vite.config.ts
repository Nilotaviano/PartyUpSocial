import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import type { Plugin } from 'vite'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'
import { pwaManifest } from './src/pwa.manifest.ts'

const packageJsonPath = fileURLToPath(
  new URL('./package.json', import.meta.url),
)
const { version: appVersion } = JSON.parse(
  readFileSync(packageJsonPath, 'utf-8'),
) as {
  version: string
}

/**
 * `devOptions.enabled` below makes the dev server compile src/sw.ts, but it serves it at
 * '/dev-sw.js?dev-sw' (the plugin hardcodes that name) and injects its own registration script
 * into index.html. Neither suits us: application code registers '/sw.js' by hand, and a dev page
 * load must install nothing at all — a shell worker caching the document would shadow Vite's
 * module graph and HMR. So this plugin serves the compiled worker under the '/sw.js' the push
 * opt-in flow asks for, and strips the plugin's auto-registration script back out.
 */
function devServiceWorkerUrl(): Plugin {
  return {
    name: 'partyup:dev-service-worker-url',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use((request, _response, next) => {
        if (request.url === '/sw.js') {
          request.url = '/dev-sw.js?dev-sw'
        }
        next()
      })
    },
    transformIndexHtml: {
      order: 'post',
      handler: (html) =>
        html.replace(
          /<script id="vite-plugin-pwa:register-dev-sw"[\s\S]*?<\/script>/,
          '',
        ),
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    // `injectManifest`, not `generateSW`: src/sw.ts is a hand-written worker (push handlers
    // plus a hand-rolled shell precache) and the plugin only compiles it and substitutes
    // `self.__WB_MANIFEST`. `injectRegister: false` keeps registration in application code —
    // src/main.tsx registers the shell worker, and the push opt-in flow registers the same
    // '/sw.js' URL on grant, so the emitted filename must stay sw.js.
    VitePWA({
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      injectRegister: false,
      // Dev compiles the worker too, so '/sw.js' resolves locally (see devServiceWorkerUrl
      // above, which maps the URL and removes the plugin's own auto-registration). The dev file
      // is served as an ES module (`type: 'module'` is what injectManifest needs), which the
      // classic-script register('/sw.js') in pushSubscription.ts cannot parse -- exercising the
      // real push registration locally needs a production build (npm run build + preview).
      devOptions: { enabled: true, type: 'module' },
      injectManifest: {
        // Minimal shell: the document, the JS/CSS bundles and the brand marks. Web fonts stay
        // network-only — the fontsource variable faces ship as dozens of unicode subsets, and
        // an offline boot renders fine on the fallback stack.
        globPatterns: ['**/*.{js,css,html}', 'favicon.svg', 'icon-*.png'],
      },
      manifest: pwaManifest,
    }),
    // After VitePWA: both hooks are `order: 'post'`, so plugin order is what decides which one
    // sees the other's HTML.
    devServiceWorkerUrl(),
  ],
  // D-212's client-side context field: a build-time constant baked from package.json's
  // version, replaced by esbuild wherever `__APP_VERSION__` is referenced (declared for
  // TypeScript in src/vite-env.d.ts). Shared by `test` below since both live in this one
  // config, so vitest sees the same constant as the production build.
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
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
