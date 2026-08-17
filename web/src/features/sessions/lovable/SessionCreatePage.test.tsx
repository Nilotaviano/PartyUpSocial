import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

const navigate = vi.hoisted(() => vi.fn())
vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router')
  return { ...actual, useNavigate: () => navigate }
})

import '../../../i18n'
import { SessionCreatePage } from './SessionCreatePage'

function renderPage(initialEntries: string[] = ['/sessions/new']) {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={initialEntries}>
        <SessionCreatePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

const friendsResponse = {
  items: [
    {
      friendshipId: 'friendship-1',
      userId: 'user-1',
      username: 'friend.one',
      displayName: 'Friend One',
      avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
      platforms: [],
    },
  ],
}

// The Steam arm's SteamGamePicker fires its own `client.GET` calls against
// `/api/v1/steam-catalog` once a game name reaches 2 characters. Dispatching
// `api.get` by path (rather than one flat `mockResolvedValue`) keeps that
// traffic from being mistaken for the friends-list fetch and lets each test
// choose whether the catalog is available.
function mockGet({
  friends = { items: [] } as unknown,
  catalogAvailable = false,
  catalogResults = [] as { appId: number; name: string }[],
} = {}) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/steam-catalog') {
      return Promise.resolve({
        data: { catalogAvailable, results: catalogResults },
      })
    }
    return Promise.resolve({ data: friends })
  })
}

function selectPrivate() {
  fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
}

beforeEach(() => {
  mockGet()
  api.post.mockResolvedValue({
    data: {
      session: { id: 'session-1' },
      guestLinks: [],
    },
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
  vi.useRealTimers()
})

describe('SessionCreatePage (lovable)', () => {
  it('focuses the page h1 on mount_D139', async () => {
    renderPage()

    const heading = await screen.findByRole('heading', { level: 1 })
    await waitFor(() => expect(heading).toHaveFocus())
  })

  it('shows the fixed-session defaults_D129', async () => {
    renderPage()

    expect(await screen.findByText(/4 minimum, 4 maximum, 2 hours/i)).toBeInTheDocument()
    expect(screen.getByLabelText('Minimum attendance')).toHaveValue(4)
    expect(screen.getByLabelText('Maximum capacity')).toHaveValue(4)
    expect(screen.getByLabelText('Duration (minutes)')).toHaveValue(120)
  })

  it('states the minutes unit in the duration field accessible name', async () => {
    renderPage()

    const duration = await screen.findByRole('spinbutton', { name: /minutes/i })

    expect(duration).toHaveAttribute('id', 'field-duration')
    expect(duration).toHaveValue(120)
  })

  it('rejects a duration outside the 15-minute increment and blocks submission_D129', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.change(screen.getByLabelText('Duration (minutes)'), { target: { value: '10' } })

    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('requires an invited friend for a non-private session and creates one_D129', async () => {
    mockGet({ friends: friendsResponse })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      (await screen.findAllByText('A non-private session needs at least one invited friend.'))
        .length,
    ).toBeGreaterThan(0)

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({ body: expect.objectContaining({ inviteeUserIds: ['user-1'] }) }),
      ),
    )
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/sessions/session-1'))
  })

  it('links every validation-summary entry to an existing field id_H2', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Duration (minutes)'), { target: { value: '10' } })
    fireEvent.change(screen.getByLabelText('Minimum attendance'), { target: { value: '0' } })
    fireEvent.change(screen.getByLabelText('Guest links'), { target: { value: '25' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    const summary = await screen.findByText('There were problems with your submission')
    const links = within(summary.closest('div')!).getAllByRole('link')
    expect(links.length).toBeGreaterThan(0)
    for (const link of links) {
      const targetId = link.getAttribute('href')!.slice(1)
      expect(document.getElementById(targetId)).not.toBeNull()
    }
  })

  it('renders the Steam game picker combobox and the Other platform option through i18next_H5', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })

    expect(screen.getByRole('combobox', { name: 'Search the Steam catalog' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Other' })).toBeInTheDocument()
  })

  it('does not render a catalog/free-text radio choice for the game field_D176', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })

    // The visibility segmented control is the only `role="radio"` group on this
    // page's happy path (the game field itself never grows a radio choice).
    for (const radio of screen.getAllByRole('radio')) {
      expect(radio).toHaveAccessibleName(/^(Private|Groups|Public)$/)
    }
  })

  it('submits steamAppId as null when the free-typed game name is never selected from the catalog_D176_D194', async () => {
    mockGet({ friends: friendsResponse })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('selecting a catalog result fills name+appId and submits both_D194', async () => {
    mockGet({
      friends: friendsResponse,
      catalogAvailable: true,
      catalogResults: [{ appId: 730, name: 'Counter-Strike 2' }],
    })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'counter' },
    })
    fireEvent.mouseDown(await screen.findByRole('option', { name: 'Counter-Strike 2' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: '730' },
          }),
        }),
      ),
    )
  })

  it('retyping the name after selecting a catalog result clears the stale appId_D194', async () => {
    mockGet({
      friends: friendsResponse,
      catalogAvailable: true,
      catalogResults: [{ appId: 730, name: 'Counter-Strike 2' }],
    })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'counter' },
    })
    fireEvent.mouseDown(await screen.findByRole('option', { name: 'Counter-Strike 2' }))

    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Deep Rock Galactic' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Deep Rock Galactic', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('degrades to the manual App ID field and submits the typed digits when the catalog is unavailable_D176_D194', async () => {
    mockGet({ friends: friendsResponse })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })

    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: '730' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: '730' },
          }),
        }),
      ),
    )
  })

  it('extracts the app id from a pasted Steam store URL in the degraded arm_D176_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    const appIdField = await screen.findByLabelText('Steam app ID')

    fireEvent.change(appIdField, {
      target: { value: 'https://store.steampowered.com/app/730/CounterStrike_2/' },
    })

    expect(screen.getByLabelText('Steam app ID')).toHaveValue('730')
  })

  it('rejects a non-digit Steam app id and blocks submission in the degraded arm_D176_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    selectPrivate()
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('requires a game name when a Steam app id is typed with the name left blank in the degraded arm_F1_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    selectPrivate()
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'aa' },
    })
    const nameField = await screen.findByLabelText('Enter a game name')
    fireEvent.change(nameField, { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Steam app ID'), { target: { value: '730' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('posts the wire-format platform value, not the UI label', async () => {
    mockGet({ friends: friendsResponse })
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-09-01T18:00' },
    })
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({ body: expect.objectContaining({ platform: 'steam' }) }),
      ),
    )

    api.post.mockClear()
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Other' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({ body: expect.objectContaining({ platform: 'notApplicable' }) }),
      ),
    )
  })

  it('toggles visibility through the Private/Groups/Public segmented control_D129', async () => {
    renderPage()

    const privateRadio = screen.getByRole('radio', { name: 'Private' })
    const publicRadio = screen.getByRole('radio', { name: 'Public' })
    expect(publicRadio).toHaveAttribute('aria-checked', 'true')
    expect(privateRadio).toHaveAttribute('aria-checked', 'false')

    fireEvent.click(privateRadio)

    expect(privateRadio).toHaveAttribute('aria-checked', 'true')
    expect(publicRadio).toHaveAttribute('aria-checked', 'false')
    // A private session no longer requires an invited friend.
    expect(screen.queryByText('A non-private session needs at least one invited friend.')).not.toBeInTheDocument()
  })

  it('warns before discarding dirty create-form values_D152_D162', async () => {
    renderPage()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Draft title' } })

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
    expect(screen.getByLabelText('Title')).toHaveValue('Draft title')
    expect(cancelTrigger).toHaveFocus()
    expect(navigate).not.toHaveBeenCalled()

    fireEvent.click(cancelTrigger)
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard' }))
    expect(navigate).toHaveBeenCalledWith('/sessions/upcoming')
  })

  it('parses the prefill query and surfaces the clamp to fifteen minutes ahead_D102', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    vi.setSystemTime(new Date('2030-01-01T00:00:00.000Z'))
    const expectedStart = toLocalDatetimeString(new Date(Date.now() + 15 * 60_000))

    renderPage(['/sessions/new?startTime=2020-01-01T00%3A00%3A00.000Z&durationMinutes=90'])
    vi.useRealTimers()

    expect(await screen.findByLabelText('Duration (minutes)')).toHaveValue(90)
    expect(screen.getByLabelText('Start time')).toHaveValue(expectedStart)
    expect(
      screen.getByText(
        'The start time was moved to the earliest time allowed (15 minutes from now).',
      ),
    ).toBeInTheDocument()
  })

  it('does not surface a clamp message when the prefilled start is already far enough ahead_D102', async () => {
    renderPage(['/sessions/new?startTime=2035-06-01T12%3A00%3A00.000Z&durationMinutes=60'])

    expect(await screen.findByLabelText('Duration (minutes)')).toHaveValue(60)
    expect(
      screen.queryByText(
        'The start time was moved to the earliest time allowed (15 minutes from now).',
      ),
    ).not.toBeInTheDocument()
  })

  it('renders the over-duration warning without blocking submit_D109', async () => {
    renderPage([
      '/sessions/new?startTime=2035-06-01T12%3A00%3A00.000Z&durationMinutes=120&overDurationMinutes=30',
    ])

    expect(
      await screen.findByText('This session runs 30 minutes past the recommended window.'),
    ).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Raid night' } })
    selectPrivate()
    fireEvent.click(screen.getByRole('button', { name: 'Create session' }))

    expect(
      screen.queryByText('There were problems with your submission'),
    ).not.toBeInTheDocument()
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions',
        expect.objectContaining({ body: expect.objectContaining({ durationMinutes: 120 }) }),
      ),
    )
  })
})

function toLocalDatetimeString(date: Date) {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(
    date.getHours(),
  )}:${pad(date.getMinutes())}`
}
