import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

async function freshModule() {
  vi.resetModules()
  return import('./TimeZoneField')
}

describe('TimeZoneField zone list', () => {
  const originalSupportedValuesOf = Intl.supportedValuesOf

  afterEach(() => {
    Object.defineProperty(Intl, 'supportedValuesOf', {
      value: originalSupportedValuesOf,
      writable: true,
      configurable: true,
    })
  })

  it('always accepts the browser-resolved zone, even when supportedValuesOf reports only its canonical name', async () => {
    // Every surface prefills the browser-resolved zone. If the browser resolves a tzdata alias
    // that supportedValuesOf omits, the field would otherwise reject its own default value and
    // leave the user unable to save.
    Object.defineProperty(Intl, 'supportedValuesOf', {
      value: () => ['Asia/Kolkata', 'Europe/Lisbon'],
      writable: true,
      configurable: true,
    })
    const { isKnownTimeZone, getTimeZoneOptions } = await freshModule()
    const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone

    expect(isKnownTimeZone(browserZone)).toBe(true)
    expect(getTimeZoneOptions().some((option) => option.id === browserZone)).toBe(true)
    expect(isKnownTimeZone('UTC')).toBe(true)
  })

  it('builds the list from Intl.supportedValuesOf, one entry per real IANA zone, with its UTC offset', async () => {
    const { getTimeZoneOptions } = await freshModule()
    const options = getTimeZoneOptions()

    // The real tzdata list carries several hundred zones; a two-option stub
    // (the D-182-era onboarding <select>) would fail this immediately.
    expect(options.length).toBeGreaterThan(300)
    const saoPaulo = options.find((option) => option.id === 'America/Sao_Paulo')
    expect(saoPaulo).toBeDefined()
    expect(saoPaulo!.label).toMatch(/^America\/Sao_Paulo \(GMT/)
  })

  it('falls back to the browser-resolved zone plus UTC when Intl.supportedValuesOf is unavailable', async () => {
    Object.defineProperty(Intl, 'supportedValuesOf', {
      value: undefined,
      writable: true,
      configurable: true,
    })
    const { getTimeZoneOptions, isKnownTimeZone } = await freshModule()
    const options = getTimeZoneOptions()
    const ids = options.map((option) => option.id)

    expect(ids).toContain(Intl.DateTimeFormat().resolvedOptions().timeZone)
    expect(ids).toContain('UTC')
    expect(ids.length).toBeLessThanOrEqual(2)
    expect(isKnownTimeZone('America/Sao_Paulo')).toBe(false)
  })

  it('keeps a stored-but-unlisted zone selectable and valid without recomputing the rest of the list', async () => {
    const { getTimeZoneOptions, isKnownTimeZone } = await freshModule()
    const withoutStored = getTimeZoneOptions()
    const withStored = getTimeZoneOptions('Region/Legacy_Zone')

    expect(withoutStored.some((option) => option.id === 'Region/Legacy_Zone')).toBe(false)
    expect(withStored.some((option) => option.id === 'Region/Legacy_Zone')).toBe(true)
    expect(withStored.length).toBe(withoutStored.length + 1)
    // One bad/unlisted zone id (offset computation throws for it) must not
    // blank the rest of the list — every previously-present option survives.
    for (const option of withoutStored) {
      expect(withStored.some((candidate) => candidate.id === option.id)).toBe(true)
    }
    expect(isKnownTimeZone('Region/Legacy_Zone')).toBe(false)
    expect(isKnownTimeZone('Region/Legacy_Zone', 'Region/Legacy_Zone')).toBe(true)
    expect(isKnownTimeZone('America/Sao_Paulo')).toBe(true)
    expect(isKnownTimeZone('Not/A_Real_Zone')).toBe(false)
  })
})

describe('TimeZoneField markup', () => {
  afterEach(() => {
    cleanup()
  })

  it('submits the raw IANA id as each option value while showing the offset only in its text', async () => {
    const { TimeZoneField } = await freshModule()
    render(
      <TimeZoneField
        defaultValue="America/Sao_Paulo"
        hint="Type to filter."
        id="tz"
        label="Time zone"
        name="timeZone"
      />,
    )

    const input = screen.getByLabelText('Time zone') as HTMLInputElement
    expect(input).toHaveAttribute('list', 'tz-options')
    expect(input).toHaveAttribute('name', 'timeZone')
    expect(input.value).toBe('America/Sao_Paulo')

    const option = document.querySelector('option[value="America/Sao_Paulo"]')
    expect(option).not.toBeNull()
    expect(option!.textContent).toMatch(/^America\/Sao_Paulo \(GMT/)
    expect(screen.getByText('Type to filter.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('wires an error message with role=alert, aria-invalid and aria-describedby', async () => {
    const { TimeZoneField } = await freshModule()
    render(
      <TimeZoneField
        defaultValue="not-a-zone"
        errorMessage="Choose a time zone from the list."
        hint="Type to filter."
        id="tz"
        label="Time zone"
        name="timeZone"
      />,
    )

    const input = screen.getByLabelText('Time zone')
    expect(input).toHaveAttribute('aria-invalid', 'true')
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('Choose a time zone from the list.')
    expect(input.getAttribute('aria-describedby')).toContain(alert.id)
  })
})
