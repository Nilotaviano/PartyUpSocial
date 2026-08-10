import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
}))

import '../../i18n'
import { viewerProfileKey } from '../sessions/sessionQueries'
import { TourOverlay } from './TourOverlay'

Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: vi.fn() })

const STEP_NAMES = [
  'Home',
  'Friends',
  'Sessions',
  'Polls',
  'Calendar',
  'Discover',
  'Notifications',
  'Settings',
]

function LocationState() {
  const location = useLocation()
  return <output data-testid="location-state">{JSON.stringify(location.state)}</output>
}

/** The chrome elements the tour spotlights — a stand-in for `AuthenticatedShell`'s real nav. */
function ChromeStub() {
  return (
    <nav>
      <a data-tour="home" href="/app" tabIndex={-1}>Home</a>
      <a data-tour="friends" href="/friends" tabIndex={-1}>Friends</a>
      <a data-tour="sessions" href="/sessions" tabIndex={-1}>Sessions</a>
      <a data-tour="polls" href="/polls" tabIndex={-1}>Polls</a>
      <a data-tour="calendar" href="/calendar" tabIndex={-1}>Calendar</a>
      <a data-tour="discover" href="/discover" tabIndex={-1}>Discover</a>
      <a data-tour="notifications" href="/notifications" tabIndex={-1}>Notifications</a>
      <a data-tour="settings" href="/settings/account" tabIndex={-1}>Settings</a>
    </nav>
  )
}

function renderOverlay(
  initialEntry: string | { pathname: string; state?: unknown } = '/app',
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }),
) {
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <ChromeStub />
        <TourOverlay />
        <LocationState />
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { ...utils, queryClient }
}

/**
 * Lets any pending React work (effects scheduled via `queueMicrotask`, query-cache-driven
 * re-renders) actually flush and paint before the caller asserts — a bare synchronous assert right
 * after an `await` on the query client can pass even when the gate that should suppress a reopen is
 * deleted, because the state update it would have skipped hasn't committed yet.
 */
async function settle(ms = 50) {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms))
  })
}

function mockProfile(tourCompleted: boolean) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/me') return Promise.resolve({ data: { tourCompleted } })
    throw new Error(`Unexpected GET ${path}`)
  })
}

beforeEach(() => {
  api.post.mockResolvedValue({ data: undefined })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('TourOverlay — §1 gate (D-182)', () => {
  it('never opens while the profile query is pending', async () => {
    api.get.mockImplementation(() => new Promise(() => {}))
    renderOverlay()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/me'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('never opens when the profile query errors — fail-closed', async () => {
    api.get.mockRejectedValue(new Error('boom'))
    renderOverlay()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/me'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('never opens when tourCompleted is already true', async () => {
    mockProfile(true)
    renderOverlay()

    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/v1/me'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('opens when the profile query succeeds and tourCompleted is false', async () => {
    mockProfile(false)
    renderOverlay()

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('Step 1 of 8: Home')).toBeInTheDocument()
  })

  it('never renders outside /app even with a gate-satisfying profile', async () => {
    mockProfile(false)
    renderOverlay('/friends')

    await waitFor(() => expect(api.get).not.toHaveBeenCalled())
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('TourOverlay — step order (D-182)', () => {
  it('walks all eight stops in nav order, Discover included at position 6', async () => {
    mockProfile(false)
    renderOverlay()

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    for (let index = 0; index < STEP_NAMES.length; index++) {
      const name = STEP_NAMES[index]
      expect(
        screen.getByText(`Step ${index + 1} of 8: ${name}`),
      ).toBeInTheDocument()
      if (index < STEP_NAMES.length - 1) {
        fireEvent.click(screen.getByRole('button', { name: 'Next' }))
      }
    }

    expect(screen.getByRole('button', { name: 'Finish' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument()
  })
})

describe('TourOverlay — finish and skip (D-182)', () => {
  it('finishing POSTs completion, optimistically marks the cache, and the dismissed ref survives a focus refetch', async () => {
    mockProfile(false)
    const { queryClient } = renderOverlay()

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    for (let index = 0; index < STEP_NAMES.length - 1; index++) {
      fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    }
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/me/tour-completed',
        expect.objectContaining({ params: { header: { 'X-CSRF-TOKEN': '' } } }),
      ),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(queryClient.getQueryData(viewerProfileKey)).toEqual({ tourCompleted: true })

    // Simulated window-focus refetch: the server still reports false (the design's own accepted
    // residual for a failed POST) — the in-memory dismissed ref, not the query cache, must keep
    // the tour shut for the rest of this page load. `settle` gives the gate effect a real chance to
    // run and reopen the dialog before the assertion — without it, a deleted `!dismissedRef.current
    // &&` guard would still read as green because the state update hasn't committed yet.
    mockProfile(false)
    await act(() => queryClient.refetchQueries({ queryKey: viewerProfileKey }))
    await settle()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('skipping from step 1 also POSTs completion and sets the dismissed ref', async () => {
    mockProfile(false)
    const { queryClient } = renderOverlay()

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Skip tour' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/me/tour-completed',
        expect.objectContaining({ params: { header: { 'X-CSRF-TOKEN': '' } } }),
      ),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    mockProfile(false)
    await act(() => queryClient.refetchQueries({ queryKey: viewerProfileKey }))
    await settle()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('Esc skips (the primitive default) — completion still POSTs', async () => {
    mockProfile(false)
    renderOverlay()

    const dialog = await screen.findByRole('dialog')
    fireEvent.keyDown(dialog, { key: 'Escape', code: 'Escape' })

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/me/tour-completed',
        expect.anything(),
      ),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })
})

describe('TourOverlay — overrides (D-182)', () => {
  it('an outside click is inert — the dialog stays open', async () => {
    mockProfile(false)
    renderOverlay()

    await screen.findByRole('dialog')
    // Radix's Dialog configures its DismissableLayer with `deferPointerDownOutside: true`, so a
    // genuine outside dismiss needs a real main-button pointerdown FOLLOWED BY a click (the
    // pointerdown alone only arms it), and the layer's own pointerdown listener registers a
    // setTimeout(0) after mount — `settle` before and after gives both real timers a chance to run.
    await settle()
    fireEvent.pointerDown(document.body, { pointerType: 'mouse', button: 0 })
    fireEvent.click(document.body, { button: 0 })
    await settle()

    expect(api.post).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('closing focuses the Home link (no trigger element to restore focus to)', async () => {
    mockProfile(false)
    renderOverlay()

    await screen.findByRole('dialog')
    fireEvent.click(screen.getByRole('button', { name: 'Skip tour' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() =>
      expect(screen.getByText('Home', { selector: '[data-tour="home"]' })).toHaveFocus(),
    )
  })
})

describe('TourOverlay — Replay (D-182)', () => {
  it('opens regardless of tourCompleted, clears the dismissed ref, and consumes the router state once', async () => {
    mockProfile(true)
    renderOverlay({ pathname: '/app', state: { replayTour: true } })

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('Step 1 of 8: Home')).toBeInTheDocument()

    // Router-native consumption: `navigate(pathname, { replace: true, state: null })`, not a raw
    // `history.replaceState` — the state is gone from `useLocation()` after the open completes.
    // A real reload re-requests `/app` with whatever `history.state` `replace` last wrote, so a
    // consumed (`null`) state is the actual guarantee against re-triggering on reload — proven here
    // directly rather than by a second, disconnected `MemoryRouter` mount (which only re-proves the
    // ordinary §1 gate, not consumption).
    await waitFor(() => expect(screen.getByTestId('location-state').textContent).toBe('null'))
  })
})
