import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
const navigateSpy = vi.hoisted(() => vi.fn())

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) =>
    Boolean(error) && typeof error === 'object' && 'code' in (error as object),
}))

vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router')
  return { ...actual, useNavigate: () => navigateSpy }
})

import '../../i18n'
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

function addOption(value: string) {
  fireEvent.change(screen.getByLabelText('Add time'), { target: { value } })
  fireEvent.click(screen.getByRole('button', { name: 'Add time' }))
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollCreatePage', () => {
  it('defaults the deadline choice to 24 hours_D070', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    expect(
      await screen.findByRole('combobox', { name: 'Voting deadline' }),
    ).toHaveValue('hours24')
  })

  it('requires at least two options before submit_D131', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    fireEvent.click(screen.getByRole('checkbox', { name: 'Private poll' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      (await screen.findAllByText('Add at least two candidate times.')).length,
    ).toBeGreaterThan(0)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('merges a duplicate option instant instead of erroring_D080', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    addOption('2099-01-01T10:00')
    addOption('2099-01-01T10:00')

    expect(
      await screen.findByText('That time was already added.'),
    ).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /^Remove/ })).toHaveLength(1)
  })

  it('rejects a non-private poll with zero invited friends_D125', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    addOption('2099-01-01T10:00')
    addOption('2099-01-01T12:00')
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      (
        await screen.findAllByText(
          'A non-private poll needs at least one invited friend.',
        )
      ).length,
    ).toBeGreaterThan(0)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('submits every eventual session detail alongside the poll options_D154', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    api.post.mockResolvedValue({
      data: { poll: { id: 'poll-1' }, guestLinks: [] },
    })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    addOption('2099-01-01T10:00')
    addOption('2099-01-01T12:00')
    fireEvent.change(screen.getByLabelText('Notes'), {
      target: { value: 'Bring snacks' },
    })
    fireEvent.click(screen.getByRole('checkbox', { name: 'Private poll' }))
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

  it('warns before discarding dirty create-form values_D162', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
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
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    await screen.findByLabelText('Title')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(navigateSpy).toHaveBeenCalledWith('/polls')
  })

  describe('prefill poll arm (S7, D-102/D-163, A-083)', () => {
    it('reads repeated startTimes into the option editor, deduped and sorted_D102', async () => {
      api.get.mockResolvedValue({ data: { items: [] } })
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
      api.get.mockResolvedValue({ data: { items: [] } })
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
      api.get.mockResolvedValue({ data: { items: [] } })
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
      api.get.mockResolvedValue({ data: { items: [] } })
      renderPage('/polls/new?startTimes=2099-04-01T10%3A00%3A00Z&startTimes=2099-04-02T10%3A00%3A00Z')

      await screen.findByRole('group', { name: 'Candidate times' })
      expect(
        screen.queryByText(/from your selection were kept/),
      ).not.toBeInTheDocument()
    })

    it('never surfaces a clamp notice with no prefill at all', async () => {
      api.get.mockResolvedValue({ data: { items: [] } })
      renderPage()

      await screen.findByLabelText('Title')
      expect(
        screen.queryByText(
          'The start time was moved to the earliest time allowed (15 minutes from now).',
        ),
      ).not.toBeInTheDocument()
    })
  })
})
