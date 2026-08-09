import { focusManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

import '../../i18n'
import { NotificationBell } from './NotificationBell'

function renderBell() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <NotificationBell />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('NotificationBell — ruling 1 exact count and 99+ ceiling', () => {
  it('renders the exact unread count in the badge and the live region', async () => {
    api.get.mockResolvedValue({ data: { count: 5 } })

    renderBell()

    expect(await screen.findByText('5')).toBeInTheDocument()
    await waitFor(() =>
      expect(screen.getByRole('status')).toHaveTextContent('5 unread notifications'),
    )
  })

  it('renders 99+ once the count exceeds 99', async () => {
    api.get.mockResolvedValue({ data: { count: 140 } })

    renderBell()

    expect(await screen.findByText('99+')).toBeInTheDocument()
  })

  it('renders no badge and an empty live region when unread count is zero', async () => {
    api.get.mockResolvedValue({ data: { count: 0 } })

    renderBell()

    await screen.findByRole('link', { name: 'Notifications' })
    expect(screen.queryByText('0')).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('')
  })
})

describe('NotificationBell — window-focus refetch (design §5: default QueryClient behavior)', () => {
  it('refetches the unread count when the window regains focus', async () => {
    api.get.mockResolvedValue({ data: { count: 1 } })

    renderBell()
    await screen.findByText('1')
    expect(api.get).toHaveBeenCalledTimes(1)

    api.get.mockResolvedValue({ data: { count: 3 } })
    focusManager.setFocused(false)
    focusManager.setFocused(true)

    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(2))
    expect(await screen.findByText('3')).toBeInTheDocument()
  })
})
