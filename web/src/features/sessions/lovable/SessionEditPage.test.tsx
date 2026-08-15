import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  patch: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get, PATCH: api.patch },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
  ApiError: class ApiError extends Error {
    status: number
    code: string
    constructor(status: number, code: string) {
      super(code)
      this.status = status
      this.code = code
    }
  },
}))

const navigate = vi.hoisted(() => vi.fn())
vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router')
  return { ...actual, useNavigate: () => navigate }
})

import { ApiError } from '../../../api/client'
import '../../../i18n'
import { SessionEditPage } from './SessionEditPage'

function detail(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 120,
    platform: 'Steam',
    game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
    notes: 'Bring snacks',
    minAttendance: 4,
    maxCapacity: 8,
    isPrivate: false,
    status: 'unconfirmed',
    createdAt: '2030-08-01T00:00:00Z',
    roster: [],
    version: 'v1',
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/sessions/session-1/edit']}>
        <Routes>
          <Route path="/sessions/:sessionId/edit" element={<SessionEditPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

// The mounted SteamGamePicker fires its own `client.GET` calls against
// `/api/v1/steam-catalog` (300ms after mount, since the seeded game name is
// already 2+ chars) through this same mocked `client.GET`. Dispatching by
// path keeps those calls from consuming the session-detail response queue
// tests below rely on (`mockResolvedValueOnce` chains, call-count asserts).
function mockSessionDetail(next: (call: number) => unknown) {
  let call = 0
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/steam-catalog') {
      return Promise.resolve({ data: { catalogAvailable: false, results: [] } })
    }
    call += 1
    return Promise.resolve({ data: next(call) })
  })
}

beforeEach(() => {
  mockSessionDetail(() => detail())
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionEditPage (lovable)', () => {
  it('focuses the page h1 on mount_D139', async () => {
    renderPage()

    const heading = await screen.findByRole('heading', { level: 1 })
    await waitFor(() => expect(heading).toHaveFocus())
  })

  it('maps the clear-max/notes/game checkboxes onto the clear list and omits the cleared keys from the body_A070', async () => {
    api.patch.mockResolvedValue({ data: detail({ maxCapacity: null, notes: null, game: null }) })
    renderPage()

    fireEvent.click(await screen.findByLabelText('Remove capacity limit'))
    fireEvent.click(screen.getByLabelText('Remove notes'))
    fireEvent.click(screen.getByLabelText('Remove game'))
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() =>
      expect(api.patch).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.objectContaining({
          body: expect.objectContaining({
            clear: expect.arrayContaining(['maxCapacity', 'notes', 'game']),
          }),
        }),
      ),
    )
    const body = api.patch.mock.calls[0][1].body
    expect(body).not.toHaveProperty('maxCapacity')
    expect(body).not.toHaveProperty('notes')
    expect(body).not.toHaveProperty('game')
  })

  it('routes the unlimited-capacity control through the A-070 clear list with no competing maxCapacity value_A070', async () => {
    mockSessionDetail(() => detail({ maxCapacity: 5 }))
    api.patch.mockResolvedValue({ data: detail({ maxCapacity: null }) })
    renderPage()

    fireEvent.click(await screen.findByLabelText('Remove capacity limit'))
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(1))
    expect(api.patch).toHaveBeenCalledWith(
      '/api/v1/sessions/{sessionId}',
      expect.objectContaining({
        body: {
          title: 'Raid night',
          startTime: new Date('2030-09-01T18:00').toISOString(),
          durationMinutes: 120,
          platform: 'Steam',
          minAttendance: 4,
          isPrivate: false,
          notes: 'Bring snacks',
          game: { name: 'Deep Rock Galactic', steamAppId: '548430' },
          clear: ['maxCapacity'],
          ifMatch: 'v1',
        },
      }),
    )
    expect(api.patch.mock.calls[0][1].body).not.toHaveProperty('maxCapacity')
  })

  it('selecting a catalog result fills name+appId and submits both in the game payload_D194', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/steam-catalog') {
        return Promise.resolve({
          data: { catalogAvailable: true, results: [{ appId: 1091500, name: 'Cyberpunk 2077' }] },
        })
      }
      return Promise.resolve({ data: detail() })
    })
    api.patch.mockResolvedValue({ data: detail() })
    renderPage()

    const input = await screen.findByRole('combobox')
    fireEvent.change(input, { target: { value: 'cyberpunk' } })
    const option = await screen.findByRole('option', { name: 'Cyberpunk 2077' })
    fireEvent.mouseDown(option)

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() =>
      expect(api.patch).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Cyberpunk 2077', steamAppId: '1091500' },
          }),
        }),
      ),
    )
  })

  it('retyping the seeded game name clears the stale stored appId instead of resurrecting it_D194', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/steam-catalog') {
        return Promise.resolve({
          data: { catalogAvailable: true, results: [{ appId: 1091500, name: 'Cyberpunk 2077' }] },
        })
      }
      return Promise.resolve({ data: detail() })
    })
    api.patch.mockResolvedValue({ data: detail() })
    renderPage()

    const input = await screen.findByRole('combobox')
    expect(input).toHaveValue('Deep Rock Galactic')

    fireEvent.change(input, { target: { value: 'Cyberpunk 2077 but not really' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() =>
      expect(api.patch).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Cyberpunk 2077 but not really', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('degrades to the manual App ID field once the catalog search reports catalogAvailable false_D194', async () => {
    mockSessionDetail(() => detail())
    renderPage()

    expect(await screen.findByLabelText('Steam app ID')).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('preserves the stored Steam app id on save instead of silently dropping it_D176', async () => {
    api.patch.mockResolvedValue({ data: detail() })
    renderPage()

    expect(await screen.findByLabelText('Steam app ID')).toHaveValue('548430')
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() =>
      expect(api.patch).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Deep Rock Galactic', steamAppId: '548430' },
          }),
        }),
      ),
    )
  })

  it('submits an edited Steam app id and extracts it from a pasted store URL_D176', async () => {
    api.patch.mockResolvedValue({ data: detail() })
    renderPage()

    const appIdField = await screen.findByLabelText('Steam app ID')
    fireEvent.change(appIdField, {
      target: { value: 'https://store.steampowered.com/app/1091500/CyberpunkTwoOhSeven/' },
    })
    expect(appIdField).toHaveValue('1091500')

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() =>
      expect(api.patch).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Deep Rock Galactic', steamAppId: '1091500' },
          }),
        }),
      ),
    )
  })

  it('does not anchor the validation summary at the hidden app-id field when Clear game is ticked_F4_H2', async () => {
    mockSessionDetail(() => detail({ game: { name: 'Deep Rock Galactic', steamAppId: 548430 } }))
    renderPage()

    fireEvent.click(await screen.findByLabelText('Remove game'))
    expect(screen.queryByLabelText('Steam app ID')).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    const summary = await screen.findByText('There were problems with your submission')
    const links = within(summary.closest('div')!).getAllByRole('link')
    expect(links.length).toBeGreaterThan(0)
    for (const link of links) {
      const targetId = link.getAttribute('href')!.slice(1)
      expect(document.getElementById(targetId)).not.toBeNull()
    }
  })

  it('clears a stale Steam app id error the moment Clear game hides the field_F4', async () => {
    mockSessionDetail(() => detail({ game: { name: 'Deep Rock Galactic', steamAppId: 548430 } }))
    renderPage()

    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(screen.getAllByText('Enter only the numeric Steam app ID.').length).toBeGreaterThan(0)

    fireEvent.click(screen.getByLabelText('Remove game'))

    await waitFor(() =>
      expect(screen.queryAllByText('Enter only the numeric Steam app ID.')).toHaveLength(0),
    )
  })

  it('links every validation-summary entry to an existing field id_H2', async () => {
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Minimum attendance'), { target: { value: '0' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    const summary = await screen.findByText('There were problems with your submission')
    const links = within(summary.closest('div')!).getAllByRole('link')
    expect(links.length).toBeGreaterThan(0)
    for (const link of links) {
      const targetId = link.getAttribute('href')!.slice(1)
      expect(document.getElementById(targetId)).not.toBeNull()
    }
  })

  it('shows the silent-change disclosure next to the privacy control_D123', async () => {
    renderPage()

    expect(
      await screen.findByText('This change does not notify participants.'),
    ).toBeInTheDocument()
  })

  it('toggles isPrivate through the Private/Public segmented control_D123', async () => {
    renderPage()

    const privateRadio = await screen.findByRole('radio', { name: 'Private' })
    const publicRadio = screen.getByRole('radio', { name: 'Public' })
    expect(publicRadio).toHaveAttribute('aria-checked', 'true')

    fireEvent.click(privateRadio)

    expect(privateRadio).toHaveAttribute('aria-checked', 'true')
    expect(publicRadio).toHaveAttribute('aria-checked', 'false')
  })

  it('retries exactly once after a session_conflict then surfaces the error on repeat failure_A071', async () => {
    api.patch
      .mockRejectedValueOnce(new ApiError(409, 'session_conflict'))
      .mockRejectedValueOnce(new ApiError(409, 'session_conflict'))
    mockSessionDetail(() => detail())

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(2))
    expect(
      await screen.findByText(
        'This session changed elsewhere. We refreshed it and are retrying your save.',
      ),
    ).toBeInTheDocument()
    expect(
      await screen.findByText('This session changed elsewhere. Please try again.'),
    ).toBeInTheDocument()
  })

  it('succeeds after exactly one automatic refetch-and-retry on conflict_A071', async () => {
    mockSessionDetail((call) => detail({ version: call === 1 ? 'v1' : 'v2' }))
    api.patch
      .mockRejectedValueOnce(new ApiError(409, 'session_conflict'))
      .mockResolvedValueOnce({ data: detail({ version: 'v2' }) })
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(2))
    expect(api.patch.mock.calls[1][1].body.ifMatch).toBe('v2')
  })

  it('locks the start time and minimum attendance while the session is in progress', async () => {
    const now = new Date()
    mockSessionDetail(() =>
      detail({
        startTime: new Date(now.getTime() - 5 * 60_000).toISOString(),
        status: 'confirmed',
      }),
    )
    renderPage()

    expect(await screen.findByText('The start time can no longer be changed.')).toBeInTheDocument()
    expect(
      screen.getByText('The minimum attendance can no longer be changed.'),
    ).toBeInTheDocument()
  })

  it('warns before discarding dirty edit-form values_D152_D162', async () => {
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Updated title' },
    })

    const cancelTrigger = screen.getByRole('button', { name: 'Cancel' })
    cancelTrigger.focus()
    fireEvent.click(cancelTrigger)

    let dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Discard unsaved changes?')
    await waitFor(() =>
      expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus(),
    )

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByLabelText('Title')).toHaveValue('Updated title')
    expect(navigate).not.toHaveBeenCalled()

    fireEvent.click(cancelTrigger)
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard' }))
    expect(navigate).toHaveBeenCalledWith('/sessions/session-1')
  })
})
