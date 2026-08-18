/**
 * D-218's web app manifest, kept in application source rather than inline in vite.config.ts so a
 * unit test can assert the install contract (standalone display, the brand colors and the icon
 * set) against the very object the build ships. vite.config.ts hands it to `VitePWA({ manifest })`
 * unchanged.
 *
 * The colors are src/index.css `:root --background: oklch(0.96 0.02 94.5)` resolved to sRGB — the
 * same ground scripts/generate-icons.mjs rasterizes the icons on, so the splash screen and the
 * icon share one continuous surface.
 */
interface PwaManifest {
  name: string
  short_name: string
  description: string
  display: 'standalone'
  start_url: string
  scope: string
  theme_color: string
  background_color: string
  icons: { src: string; sizes: string; type: string; purpose: 'any' | 'maskable' }[]
}

export const pwaManifest: PwaManifest = {
  name: 'PartyUp',
  short_name: 'PartyUp',
  description:
    'Plan game nights with your friends: availability, polls and RSVPs.',
  display: 'standalone',
  start_url: '/',
  scope: '/',
  theme_color: '#f6f2e3',
  background_color: '#f6f2e3',
  icons: [
    {
      src: '/icon-192.png',
      sizes: '192x192',
      type: 'image/png',
      purpose: 'any',
    },
    {
      src: '/icon-512.png',
      sizes: '512x512',
      type: 'image/png',
      purpose: 'any',
    },
    {
      src: '/icon-maskable-512.png',
      sizes: '512x512',
      type: 'image/png',
      purpose: 'maskable',
    },
  ],
}
