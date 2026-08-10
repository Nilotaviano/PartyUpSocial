import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => {
  class ApiError extends Error {
    readonly status: number
    readonly code: string
    readonly errors: Record<string, readonly string[]>

    constructor(status: number, code: string, errors: Record<string, readonly string[]> = {}) {
      super(code)
      this.name = 'ApiError'
      this.status = status
      this.code = code
      this.errors = errors
    }
  }

  return { get: vi.fn(), put: vi.fn(), ApiError }
})
const session = vi.hoisted(() => ({ useSession: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put },
  ApiError: api.ApiError,
  isApiError: (error: unknown) => error instanceof api.ApiError,
}))
vi.mock('../../auth/useSession', () => ({ useSession: session.useSession, sessionQueryKey: ['session'] }))

const originalLanguage = navigator.language
const originalLanguages = navigator.languages
Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: vi.fn() })

function setNavigatorLanguages(...languages: string[]) {
  Object.defineProperty(navigator, 'language', { configurable: true, value: languages[0] })
  Object.defineProperty(navigator, 'languages', { configurable: true, value: languages })
}

async function loadI18n() {
  vi.resetModules()
  const { default: i18n } = await import('../../i18n')
  await waitFor(() => expect(i18n.isInitialized).toBe(true))
  return i18n
}

async function renderPage() {
  const { AccountSettingsPage } = await import('./AccountSettingsPage')
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><AccountSettingsPage /></MemoryRouter></QueryClientProvider>)
}

afterEach(() => {
  cleanup()
  api.get.mockReset()
  api.put.mockReset()
  session.useSession.mockClear()
  localStorage.clear()
  sessionStorage.clear()
  Object.defineProperty(navigator, 'language', { configurable: true, value: originalLanguage })
  Object.defineProperty(navigator, 'languages', { configurable: true, value: originalLanguages })
})

describe('LanguageSwitch', () => {
  it('supports the browser language, English fallback, and account override_D146', async () => {
    setNavigatorLanguages('pt-BR', 'pt-BR', 'en')
    const detected = await loadI18n()
    expect(detected.resolvedLanguage).toBe('pt-BR')

    localStorage.clear()
    setNavigatorLanguages('fr-CA', 'fr-CA')
    const fallback = await loadI18n()
    expect(fallback.resolvedLanguage).toBe('en')
  })

  it('applies an authenticated profile locale through i18n.changeLanguage_D146', async () => {
    setNavigatorLanguages('en', 'en')
    const i18n = await loadI18n()
    const changeLanguage = vi.spyOn(i18n, 'changeLanguage')
    api.get.mockResolvedValue({ data: { methods: [] } })
    session.useSession.mockReturnValue({ data: { profile: { locale: 'es' }, providers: [] } })

    await renderPage()

    await waitFor(() => expect(changeLanguage).toHaveBeenCalledWith('es'))
    expect(i18n.resolvedLanguage).toBe('es')
  })

  it('persists a selector change to /me/locale and updates i18n_D146', async () => {
    setNavigatorLanguages('en', 'en')
    const i18n = await loadI18n()
    await i18n.changeLanguage('en')
    const changeLanguage = vi.spyOn(i18n, 'changeLanguage')
    api.get.mockResolvedValue({ data: { methods: [] } })
    api.put.mockResolvedValue({})
    session.useSession.mockReturnValue({ data: { profile: null, providers: [] } })

    await renderPage()

    fireEvent.click(screen.getByRole('combobox', { name: 'Display language' }))
    fireEvent.click(screen.getAllByRole('option')[1])

    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/v1/me/locale', expect.objectContaining({ body: { locale: 'pt-BR' } })))
    expect(changeLanguage).toHaveBeenCalledWith('pt-BR')
    expect(i18n.resolvedLanguage).toBe('pt-BR')
  })
})