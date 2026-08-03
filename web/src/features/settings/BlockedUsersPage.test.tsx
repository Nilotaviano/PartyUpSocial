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

  it('renders the outbound-management empty state', async () => {
    api.get.mockResolvedValue({ data: { items: [] } })
    renderPage()

    expect(await screen.findByText('No blocked users.')).toBeInTheDocument()
  })
})
