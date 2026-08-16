import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), delete: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, DELETE: api.delete },
  isApiError: () => false,
}))

import '../../i18n'
import { BlockedUsersPage } from './BlockedUsersPage'

function renderPage() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={['/settings/blocked']}>
        <BlockedUsersPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockResolvedValue({
    data: {
      items: [
        {
          blockId: 'block-1',
          username: 'snapshot.name',
          createdAt: '2026-08-01T12:00:00Z',
        },
      ],
    },
  })
  api.delete.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('BlockedUsersPageTests', () => {
  it('Render_ManagesOutboundRowsWithoutProfileLinks_D065', async () => {
    renderPage()

    expect(await screen.findByText('@snapshot.name')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Blocked users' })).toHaveFocus()
    expect(
      screen.queryByRole('link', { name: /snapshot\.name/ }),
    ).not.toBeInTheDocument()
    expect(screen.getByText(/Blocked Aug 1, 2026/)).toBeInTheDocument()
  })

  it('BlockAndUnblock_AreAvailableInTheWebMvp_D059', async () => {
    renderPage()

    expect(
      await screen.findByText(/Block people from a profile or friend card/),
    ).toBeInTheDocument()
    fireEvent.click(
      await screen.findByRole('button', { name: 'Unblock @snapshot.name' }),
    )

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith('/api/v1/blocks/{blockId}', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { blockId: 'block-1' },
        },
      }),
    )
    expect(screen.getByText('User unblocked.')).toBeInTheDocument()
  })

  it('renders a neutral disc seeded from the username snapshot, never the userId_D192', async () => {
    renderPage()

    const row = (await screen.findByText('@snapshot.name')).closest('li')!
    const disc = row.querySelector('span[aria-hidden="true"]')
    expect(disc).not.toBeNull()
    // Masked: no emoji, no image — just the neutral glyph.
    expect(disc).toHaveTextContent('●')
    expect(disc?.querySelector('img')).toBeNull()
  })

  it('scopes the unblock pending state to the row clicked, not every row (Wave 6)', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          { blockId: 'block-1', username: 'alpha.one', createdAt: '2026-08-01T12:00:00Z' },
          { blockId: 'block-2', username: 'beta.two', createdAt: '2026-08-02T12:00:00Z' },
        ],
      },
    })
    api.delete.mockImplementation(() => new Promise(() => {}))
    renderPage()

    const alphaButton = await screen.findByRole('button', { name: 'Unblock @alpha.one' })
    const betaButton = await screen.findByRole('button', { name: 'Unblock @beta.two' })
    fireEvent.click(alphaButton)

    await waitFor(() => expect(alphaButton).toBeDisabled())
    expect(betaButton).not.toBeDisabled()
  })

  it('renders the outbound-management empty state', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    expect(await screen.findByText('No blocked users.')).toBeInTheDocument()
  })

  it('renders the blocked-at date in the viewer profile time zone rather than the browser zone_D038_D046', async () => {
    const createdAt = '2026-08-01T12:00:00Z'
    const format = (timeZone?: string) =>
      new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeZone }).format(new Date(createdAt))
    const hostRender = format()
    // A profile zone whose rendering provably differs from the host zone, so the
    // assertion fails if the component silently falls back to the browser zone.
    const profileZone = ['Pacific/Midway', 'Pacific/Kiritimati'].find(
      (zone) => format(zone) !== hostRender,
    )!
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: { timeZone: profileZone } })
      }
      return Promise.resolve({
        data: { items: [{ blockId: 'block-1', username: 'snapshot.name', createdAt }] },
      })
    })
    renderPage()

    expect(
      await screen.findByText(`Blocked ${format(profileZone)}`),
    ).toBeInTheDocument()
  })
})
