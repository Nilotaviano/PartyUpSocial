import { describe, expect, it } from 'vitest'

import { pwaManifest } from './pwa.manifest'

/**
 * Every file sitting in web/public, keyed by the URL path it is served under. Vite resolves the
 * glob at transform time, so an icon the manifest names but nobody ships simply has no key here —
 * which is the point: the install prompt shows a broken icon rather than failing loudly.
 */
const publicFiles = import.meta.glob('../public/*')
const publicPaths = new Set(
  Object.keys(publicFiles).map((key) => key.replace('../public', '')),
)

describe('PWA manifest (D-218)', () => {
  it('installs as a standalone app rooted at the origin_D218', () => {
    expect(pwaManifest.name).toBe('PartyUp')
    expect(pwaManifest.display).toBe('standalone')
    expect(pwaManifest.start_url).toBe('/')
    expect(pwaManifest.scope).toBe('/')
  })

  it('paints the splash screen on the brand ground_D218', () => {
    expect(pwaManifest.theme_color).toBe('#f6f2e3')
    expect(pwaManifest.background_color).toBe('#f6f2e3')
  })

  it('declares exactly the 192, 512 and maskable-512 icons_D218', () => {
    expect(pwaManifest.icons).toEqual([
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
    ])
  })

  it('ships every icon file it names_D218', () => {
    expect(publicPaths.size).toBeGreaterThan(0)
    for (const icon of pwaManifest.icons) {
      expect([...publicPaths]).toContain(icon.src)
    }
  })
})
