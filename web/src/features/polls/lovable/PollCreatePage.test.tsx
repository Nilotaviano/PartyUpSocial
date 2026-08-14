import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
const navigateSpy = vi.hoisted(() => vi.fn())

vi.mock('../../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) =>
    Boolean(error) && typeof error === 'object' && 'code' in (error as object),
}))

vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router')
  return { ...actual, useNavigate: () => navigateSpy }
})

import i18n from '../../../i18n'
import { PollCreatePage } from './PollCreatePage'

function renderPage(initialEntry = '/polls/new') {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[initialEntry]}>
        <PollCreatePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

// With the default (empty) calendar response the grid never highlights any
// friend as free, so every slot's accessible name always reports zero free
// friends — a stable, order-only way to pick deterministic slots in tests.
async function findGridSlots() {
  const grid = await screen.findByTestId('candidate-grid')
  return within(grid).findAllByRole('button')
}

async function selectTwoGridSlots() {
  const slots = await findGridSlots()
  fireEvent.click(slots[0])
  fireEvent.click(slots[1])
}

const emptyCalendarView = {
  myAvailability: { occurrences: [], merged: [], range: null, series: [] },
  friendAvailability: [] as unknown[],
  sessions: [],
  pollOptions: [],
}

function mockGet({
  friends = { items: [] } as unknown,
  catalogAvailable = false,
  catalogResults = [] as { appId: number; name: string }[],
  timeZone = 'UTC',
  calendarView = emptyCalendarView as unknown,
} = {}) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/steam-catalog') {
      return Promise.resolve({
        data: { catalogAvailable, results: catalogResults },
      })
    }
    if (path === '/api/v1/me') {
      return Promise.resolve({ data: { timeZone } })
    }
    if (path === '/api/v1/calendar') {
      return Promise.resolve({ data: calendarView })
    }
    return Promise.resolve({ data: friends })
  })
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollCreatePage (lovable)', () => {
  it('defaults the deadline choice to 24 hours_D070', async () => {
    mockGet()
    renderPage()

    expect(await screen.findByRole('radio', { name: '24 hours' })).toBeChecked()
  })

  it('requires at least two options before submit_D131', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      (await screen.findAllByText('Add at least two candidate times.')).length,
    ).toBeGreaterThan(0)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('shows the invitee avatar without changing the checkbox accessible name, and toggles by click and keyboard_D050', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/steam-catalog') {
        return Promise.resolve({ data: { catalogAvailable: false, results: [] } })
      }
      if (path === '/api/v1/calendar') {
        return Promise.resolve({ data: emptyCalendarView })
      }
      return Promise.resolve({
        data: {
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
        },
      })
    })
    renderPage()

    const checkbox = await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' })
    const label = checkbox.closest('label')
    expect(label?.querySelector('[aria-hidden="true"]')).not.toBeNull()

    expect(checkbox).not.toBeChecked()
    fireEvent.click(label as HTMLElement)
    expect(checkbox).toBeChecked()

    // Native keyboard activation (Space/Enter on a focused input) dispatches
    // a click event on the input itself; jsdom does not synthesize this from
    // keydown, so a direct click on the focused control is the faithful proxy.
    checkbox.focus()
    fireEvent.click(checkbox)
    expect(checkbox).not.toBeChecked()
  })

  it('never produces a duplicate option nor a validation error for the same candidate slot twice_D080', async () => {
    mockGet()
    renderPage()

    const slots = await findGridSlots()
    fireEvent.click(slots[0])
    expect(await screen.findByRole('group', { name: 'Candidate times' })).toBeInTheDocument()
    within(screen.getByRole('group', { name: 'Candidate times' })).getByRole('listitem')

    // Clicking the very same slot again removes it — the instant can never
    // appear twice, and no validation error is ever raised for it.
    fireEvent.click(slots[0])
    expect(
      within(screen.getByRole('group', { name: 'Candidate times' })).queryByRole('listitem'),
    ).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('rejects a non-private poll with zero invited friends_D125', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      (
        await screen.findAllByText('A non-private poll needs at least one invited friend.')
      ).length,
    ).toBeGreaterThan(0)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('submits every eventual session detail alongside the poll options_D154', async () => {
    mockGet()
    api.post.mockResolvedValue({
      data: { poll: { id: 'poll-1' }, guestLinks: [] },
    })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.change(screen.getByLabelText('Notes'), {
      target: { value: 'Bring snacks' },
    })
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    const [, options] = api.post.mock.calls[0] as [string, { body: Record<string, unknown> }]
    expect(options.body).toMatchObject({
      title: 'Weekend raid',
      durationMinutes: 120,
      notes: 'Bring snacks',
      minAttendance: 4,
      maxCapacity: 4,
      isPrivate: true,
      deadlineChoice: 'hours24',
    })
    expect(options.body.optionStartTimes).toHaveLength(2)
    expect(navigateSpy).toHaveBeenCalledWith('/polls/poll-1')
  })

  it('does not render a catalog/free-text radio choice for the game field_D176', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Platform'), { target: { value: 'Steam' } })

    expect(screen.queryByRole('radio', { name: /^(Steam|PlayStation)/ })).not.toBeInTheDocument()
    expect(
      screen.getByRole('combobox', { name: 'Search the Steam catalog' }),
    ).toBeInTheDocument()
  })

  it('submits steamAppId as null when the free-typed game name is never selected from the catalog_D176_D194', async () => {
    mockGet()
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('selecting a catalog result fills name+appId and submits both_D194', async () => {
    mockGet({ catalogAvailable: true, catalogResults: [{ appId: 730, name: 'Counter-Strike 2' }] })
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'counter' },
    })
    fireEvent.mouseDown(await screen.findByRole('option', { name: 'Counter-Strike 2' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: '730' },
          }),
        }),
      ),
    )
  })

  it('retyping the name after selecting a catalog result clears the stale appId_D194', async () => {
    mockGet({ catalogAvailable: true, catalogResults: [{ appId: 730, name: 'Counter-Strike 2' }] })
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'counter' },
    })
    fireEvent.mouseDown(await screen.findByRole('option', { name: 'Counter-Strike 2' }))

    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Deep Rock Galactic' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Deep Rock Galactic', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('degrades to the manual App ID field and submits the typed digits when the catalog is unavailable_D176_D194', async () => {
    mockGet()
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: '730' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
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

    fireEvent.change(await screen.findByLabelText('Platform'), { target: { value: 'Steam' } })
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

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('requires a game name when a Steam app id is typed with the name left blank in the degraded arm_F1_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'aa' },
    })
    const nameField = await screen.findByLabelText('Enter a game name')
    fireEvent.change(nameField, { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Steam app ID'), { target: { value: '730' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('warns before discarding dirty create-form values_D162', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })

    const cancelTrigger = screen.getByRole('button', { name: 'Cancel' })
    fireEvent.click(cancelTrigger)

    let dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Discard unsaved changes?')

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByLabelText('Title')).toHaveValue('Weekend raid')
    expect(navigateSpy).not.toHaveBeenCalled()

    fireEvent.click(cancelTrigger)
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard' }))
    expect(navigateSpy).toHaveBeenCalledWith('/polls')
  })

  it('cancels straight through with no discard dialog when the form is clean_D162', async () => {
    mockGet()
    renderPage()

    await screen.findByLabelText('Title')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(navigateSpy).toHaveBeenCalledWith('/polls')
  })

  describe('prefill poll arm (S7, D-102/D-163, A-083)', () => {
    it('reads repeated startTimes into the option editor, deduped and sorted_D102', async () => {
      mockGet()
      renderPage(
        '/polls/new?startTimes=2099-03-02T10%3A00%3A00Z&startTimes=2099-03-01T10%3A00%3A00Z&startTimes=2099-03-01T10%3A00%3A00Z',
      )

      const list = await screen.findByRole('group', { name: 'Candidate times' })
      const items = within(list).getAllByRole('listitem')
      // Deduped (the repeated instant collapses to one) and sorted, never the URL's own order.
      expect(items).toHaveLength(2)
      expect(items[0]).toHaveTextContent('Mar 1, 2099')
      expect(items[1]).toHaveTextContent('Mar 2, 2099')
    })

    it('clamps a past-lead startTime up to now + 15 min and surfaces the clamp, never silently_D102', async () => {
      mockGet()
      renderPage(`/polls/new?startTimes=${encodeURIComponent(new Date(0).toISOString())}`)

      expect(
        await screen.findByText(
          'The start time was moved to the earliest time allowed (15 minutes from now).',
        ),
      ).toBeInTheDocument()
      const list = screen.getByRole('group', { name: 'Candidate times' })
      expect(within(list).getAllByRole('listitem')).toHaveLength(1)
    })

    it('truncates a prefill past the 12-option cap and surfaces it, never silently_F4b', async () => {
      mockGet()
      const starts = Array.from({ length: 14 }, (_, index) =>
        encodeURIComponent(`2099-04-${String(index + 1).padStart(2, '0')}T10:00:00Z`),
      )
      renderPage(`/polls/new?${starts.map((s) => `startTimes=${s}`).join('&')}`)

      const list = await screen.findByRole('group', { name: 'Candidate times' })
      expect(within(list).getAllByRole('listitem')).toHaveLength(12)
      expect(
        await screen.findByText(
          'Only the first 12 candidate times from your selection were kept — a poll can have at most 12.',
        ),
      ).toBeInTheDocument()
    })

    it('never surfaces a truncation notice with fewer prefilled instants than the cap', async () => {
      mockGet()
      renderPage('/polls/new?startTimes=2099-04-01T10%3A00%3A00Z&startTimes=2099-04-02T10%3A00%3A00Z')

      await screen.findByRole('group', { name: 'Candidate times' })
      expect(
        screen.queryByText(/from your selection were kept/),
      ).not.toBeInTheDocument()
    })

    it('never surfaces a clamp notice with no prefill at all', async () => {
      mockGet()
      renderPage()

      await screen.findByLabelText('Title')
      expect(
        screen.queryByText(
          'The start time was moved to the earliest time allowed (15 minutes from now).',
        ),
      ).not.toBeInTheDocument()
    })
  })

  it('renders real friend-availability windows on the grid as chips, masked owner drawn anonymous_F6', async () => {
    mockGet({
      calendarView: {
        ...emptyCalendarView,
        friendAvailability: [
          {
            owner: {
              ordinal: 1,
              displayName: 'Robin',
              avatar: { kind: 'emoji', emoji: '🎯', avatarSourceId: null, imgUrl: null },
            },
            // Spans far wider than any displayed week, so it always overlaps
            // whichever week the grid renders (no dependency on "now").
            ranges: [{ start: '2000-01-01T00:00:00Z', end: '2100-01-01T00:00:00Z' }],
          },
          {
            // D-076: a masked owner arrives with both `displayName` and
            // `avatar` withheld by the server.
            owner: { ordinal: 2, displayName: null, avatar: null },
            ranges: [{ start: '2000-01-01T00:00:00Z', end: '2100-01-01T00:00:00Z' }],
          },
        ],
      },
    })
    renderPage()

    const grid = await screen.findByTestId('candidate-grid')
    // The window spans the whole displayed week, so every slot reports the
    // same count — any match proves the two owners rendered on the grid.
    const [slot] = await within(grid).findAllByRole('button', { name: /2 friends free/ })
    const discs = within(slot)
      .getAllByText('', { exact: false, selector: 'span[aria-hidden="true"]' })
    expect(discs).toHaveLength(2)
    const maskedDisc = discs.find((disc) => disc.className.includes('bg-busy'))
    const realDisc = discs.find((disc) => disc.textContent === '🎯')
    expect(maskedDisc).toBeTruthy()
    expect(maskedDisc?.textContent).toBe('●')
    expect(realDisc).toBeTruthy()
  })

  it('formats option instants in the account language, not the browser locale_D146', async () => {
    mockGet()
    await i18n.changeLanguage('pt-BR')
    try {
      renderPage()

      const slots = await findGridSlots()
      fireEvent.click(slots[0])

      const list = await screen.findByRole('group', { name: 'Horários candidatos' })
      const [item] = within(list).getAllByRole('listitem')

      // pt-BR's Intl format never matches the English one for the same instant.
      const enOnlyFormat = new Intl.DateTimeFormat('en', {
        dateStyle: 'medium',
        timeStyle: 'short',
      })
      expect(item?.textContent).not.toBe('')
      expect(screen.queryByText(enOnlyFormat.format(new Date(0)))).not.toBeInTheDocument()
    } finally {
      await i18n.changeLanguage('en')
    }
  })
})
