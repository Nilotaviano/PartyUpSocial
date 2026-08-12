import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

class TestApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string) {
    super(code)
    this.status = status
    this.code = code
  }
}

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) => error instanceof TestApiError,
}))

import '../../i18n'
import { AdminUserDetailPage } from './AdminUserDetailPage'

const userId = '0198c2f0-0000-7000-8000-000000000001'

function adminUser(overrides: Record<string, unknown> = {}) {
  return {
    id: userId,
    username: 'ripley',
    displayName: 'Ellen Ripley',
    email: 'e***@n***.example',
    created: '2026-01-04T09:00:00Z',
    lifecycle: 'active',
    bannedAt: null,
    lastSeenAt: '2026-08-10T18:30:00Z',
    sessionsOrganized: 5,
    sessionsJoined: 12,
    friendCount: 7,
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[`/admin/users/${userId}`]}>
        <Routes>
          <Route path="/admin/users/:userId" element={<AdminUserDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AdminUserDetailPage — D-179 per-user stats', () => {
  it('renders every stat the widened projection carries_D179', async () => {
    api.get.mockResolvedValue({ data: adminUser() })

    renderPage()

    expect(await screen.findByRole('heading', { name: 'Ellen Ripley', level: 1 })).toBeTruthy()
    for (const label of [
      'Username',
      'Display name',
      'Email (masked)',
      'Registered',
      'Last activity',
      'Sessions organized',
      'Sessions joined',
      'Friends',
      'Account state',
      'Ban state',
    ]) {
      expect(screen.getByText(label)).toBeTruthy()
    }
    expect(screen.getByText('e***@n***.example')).toBeTruthy()
    expect(screen.getByText('Not banned')).toBeTruthy()
  })
})

describe('AdminUserDetailPage — D-180 action separation', () => {
  it('offers ban and content removal as two separate actions, with no removal option inside the ban dialog_D180', async () => {
    api.get.mockResolvedValue({ data: adminUser() })

    renderPage()

    const ban = await screen.findByRole('button', { name: 'Ban this account' })
    expect(screen.getByRole('button', { name: 'Ban and remove content' })).toBeTruthy()

    fireEvent.click(ban)

    const dialog = await screen.findByRole('dialog')
    expect(dialog.textContent).toContain('Ban this account?')
    // The ban confirmation carries no checkbox at all — content removal can
    // never ride along on it (IMPLEMENTATION_PLAN.md:149).
    expect(dialog.querySelector('[role="checkbox"]')).toBeNull()
    expect(dialog.textContent).not.toContain('remove')
  })

  it('bans without requesting content removal on the wire_D180', async () => {
    api.get.mockResolvedValue({ data: adminUser() })
    api.post.mockResolvedValue({ data: { status: 'banned' } })

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Ban this account' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Ban' }))

    await waitFor(() => expect(api.post).toHaveBeenCalled())
    expect(api.post).toHaveBeenCalledWith('/api/v1/admin/users/{id}/ban', {
      params: { header: { 'X-CSRF-TOKEN': '' }, path: { id: userId } },
      body: { removeContent: false },
    })
    expect(await screen.findByText('The account is banned.')).toBeTruthy()
  })

  it('gates content removal behind its own explicit acknowledgement_D180', async () => {
    api.get.mockResolvedValue({ data: adminUser() })
    api.post.mockResolvedValue({ data: { status: 'banned_content_removed' } })

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Ban and remove content' }))
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: 'Ban and remove content' })
    expect(confirm.hasAttribute('disabled')).toBe(true)

    fireEvent.click(
      within(dialog).getByRole('checkbox', {
        name: 'I understand the content removal is permanent.',
      }),
    )
    expect(confirm.hasAttribute('disabled')).toBe(false)

    fireEvent.click(confirm)

    await waitFor(() => expect(api.post).toHaveBeenCalled())
    expect(api.post).toHaveBeenCalledWith('/api/v1/admin/users/{id}/ban', {
      params: { header: { 'X-CSRF-TOKEN': '' }, path: { id: userId } },
      body: { removeContent: true },
    })
  })

  it('offers unban without any restoration language once the account is banned_D180', async () => {
    api.get.mockResolvedValue({ data: adminUser({ bannedAt: '2026-08-01T00:00:00Z' }) })

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Lift the ban' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog.textContent).not.toMatch(/restor/i)
    expect(screen.queryByRole('button', { name: 'Ban this account' })).toBeNull()
  })

  it('surfaces a retryable message when the removal loses the scope race_D180', async () => {
    api.get.mockResolvedValue({ data: adminUser() })
    api.post.mockRejectedValue(new TestApiError(409, 'scope_contended'))

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Ban and remove content' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(
      within(dialog).getByRole('checkbox', {
        name: 'I understand the content removal is permanent.',
      }),
    )
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Ban and remove content' }),
    )

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('try again')
  })
})

describe('AdminUserDetailPage — announcement and error hygiene', () => {
  it('clears the failed action’s alert when the next action starts', async () => {
    api.get.mockResolvedValue({ data: adminUser() })
    api.post.mockRejectedValueOnce(new TestApiError(409, 'scope_contended'))

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Ban and remove content' }))
    const removal = await screen.findByRole('dialog')
    fireEvent.click(
      within(removal).getByRole('checkbox', {
        name: 'I understand the content removal is permanent.',
      }),
    )
    fireEvent.click(
      within(removal).getByRole('button', { name: 'Ban and remove content' }),
    )
    expect((await screen.findByRole('alert')).textContent).toContain('try again')

    api.post.mockResolvedValue({ data: { status: 'banned' } })
    fireEvent.click(screen.getByRole('button', { name: 'Ban this account' }))
    const banDialog = await screen.findByRole('dialog')
    fireEvent.click(within(banDialog).getByRole('button', { name: 'Ban' }))

    expect(await screen.findByText('The account is banned.')).toBeTruthy()
    // The previous failure never renders beside the fresh success.
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('clears the previous success announcement while the next action runs', async () => {
    api.get.mockResolvedValue({ data: adminUser() })
    api.post.mockResolvedValueOnce({ data: { status: 'banned' } })

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Ban this account' }))
    fireEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'Ban' }),
    )
    expect(await screen.findByText('The account is banned.')).toBeTruthy()

    api.post.mockRejectedValue(new TestApiError(409, 'scope_contended'))
    fireEvent.click(screen.getByRole('button', { name: 'Ban and remove content' }))
    const removal = await screen.findByRole('dialog')
    fireEvent.click(
      within(removal).getByRole('checkbox', {
        name: 'I understand the content removal is permanent.',
      }),
    )
    fireEvent.click(
      within(removal).getByRole('button', { name: 'Ban and remove content' }),
    )

    await screen.findByRole('alert')
    expect(screen.queryByText('The account is banned.')).toBeNull()
  })
})

describe('AdminUserDetailPage — D-177 fail-closed parity', () => {
  it('renders a role=alert not-found message on a 404 and names no admin surface_D177', async () => {
    api.get.mockRejectedValue(new TestApiError(404, 'not_found'))

    renderPage()

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toBe('This page is not available.')
    expect(screen.queryByRole('button', { name: 'Ban this account' })).toBeNull()
    expect(document.body.textContent?.toLowerCase()).not.toContain('admin')
  })
})
