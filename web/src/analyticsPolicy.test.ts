import { describe, expect, it } from 'vitest'
import indexHtml from '../index.html?raw'
import packageJsonRaw from '../package.json?raw'

// D-161 (PHASE8_DESIGN.md §3 S1): the MVP ships no advertising or third-party analytics trackers.
// Every D-178 measure is derived server-side from product tables, so nothing the client loads may
// be a tracker. The transparency half of D-161 — the privacy policy describing what is collected —
// is S5's.
const trackerHosts = [
  'google-analytics.com',
  'googletagmanager.com',
  'doubleclick.net',
  'googlesyndication.com',
  'connect.facebook',
  'segment.com',
  'segment.io',
  'mixpanel.com',
  'posthog.com',
  'amplitude.com',
  'hotjar.com',
  'clarity.ms',
  'fullstory.com',
  'heapanalytics.com',
  'matomo',
  'plausible.io',
  'usefathom.com',
  'sentry.io',
  'browser-intake-datadoghq',
]

const trackerPackages = [
  'analytics',
  'gtag',
  'react-ga',
  'react-gtm',
  'mixpanel',
  'posthog',
  'amplitude',
  'segment',
  'hotjar',
  'clarity',
  'fullstory',
  'heap',
  'matomo',
  'plausible',
  'fathom',
  'sentry',
  'datadog',
  'logrocket',
]

const sources = import.meta.glob('./**/*.{ts,tsx}', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>

describe('D-161 analytics policy', () => {
  it('loads no third-party script or stylesheet from the SPA document_D161', () => {
    const externalUrls = [...indexHtml.matchAll(/(?:src|href)\s*=\s*"([^"]*)"/g)]
      .map((match) => match[1])
      .filter((url) => /^(https?:)?\/\//.test(url))

    expect(externalUrls).toEqual([])
  })

  it('references no known tracker host anywhere in the client source_D161', () => {
    const offenders: string[] = []
    for (const [path, contents] of Object.entries(sources)) {
      if (path.endsWith('analyticsPolicy.test.ts')) continue
      const lowered = contents.toLowerCase()
      for (const host of trackerHosts) {
        if (lowered.includes(host)) {
          offenders.push(`${path}: ${host}`)
        }
      }
    }

    expect(offenders).toEqual([])
  })

  it('declares no analytics or session-replay dependency_D161', () => {
    const manifest = JSON.parse(packageJsonRaw) as {
      dependencies?: Record<string, string>
    }
    const declared = Object.keys(manifest.dependencies ?? {})
    const offenders = declared.filter((name) =>
      trackerPackages.some((tracker) => name.toLowerCase().includes(tracker)),
    )

    expect(offenders).toEqual([])
  })
})
