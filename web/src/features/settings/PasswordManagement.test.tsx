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
  return { get: vi.fn(), post: vi.fn(), ApiError }
})

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  ApiError: api.ApiError,
  isApiError: (error: unknown) => error instanceof api.ApiError,
}))
vi.mock('../../auth/useSession', () => ({
  useSession: vi.fn().mockReturnValue({ data: { profile: null, providers: [] } }),
  sessionQueryKey: ['session'],
}))

import '../../i18n'
import i18n from '../../i18n'
import { setReauthProof } from '../../auth/reauthProof'
import { AccountSettingsPage } from './AccountSettingsPage'
import { setPendingSettingsAction } from './reauthAction'

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <AccountSettingsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(async () => {
  cleanup()
  vi.clearAllMocks()
  sessionStorage.clear()
  await i18n.changeLanguage('en')
})

describe('PasswordManagement', () => {
  it('posts the exact change-password body and renders a success message', async () => {
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] },
    })
    api.post.mockResolvedValue({ data: null })

    renderPage()

    fireEvent.change(await screen.findByLabelText('Your current password'), { target: { value: 'old-correct-password' } })
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'brand-new-password-12' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/auth/password/change',
        expect.objectContaining({
          body: { currentPassword: 'old-correct-password', newPassword: 'brand-new-password-12' },
        }),
      ),
    )
    expect(await screen.findByRole('status')).toHaveTextContent('Your password has been changed.')
    expect(screen.getByLabelText('Your current password')).toHaveValue('')
    expect(screen.getByLabelText('New password')).toHaveValue('')
  })

  it('renders the wrong-current-password copy on a reauth_required 409', async () => {
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] },
    })
    api.post.mockRejectedValue(new api.ApiError(409, 'reauth_required'))

    renderPage()

    fireEvent.change(await screen.findByLabelText('Your current password'), { target: { value: 'wrong-password' } })
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'brand-new-password-12' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('That current password is incorrect.')
  })

  it('blocks and hints a too-short new password on the change form', async () => {
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] },
    })
    api.post.mockRejectedValue(new api.ApiError(400, 'auth_validation'))

    renderPage()

    const newPasswordField = (await screen.findByLabelText('New password')) as HTMLInputElement
    expect(newPasswordField).toHaveAttribute('minlength', '12')
    expect(newPasswordField).toHaveAttribute('aria-describedby', 'password-change-new-hint')
    fireEvent.change(screen.getByLabelText('Your current password'), { target: { value: 'old-correct-password' } })
    fireEvent.change(newPasswordField, { target: { value: 'short' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Password must be at least 12 characters.')
  })

  it('renders the set-password form for an account without a password and posts after reauth', async () => {
    // /auth/methods emits every kind unconditionally; a passwordless account is a password entry
    // with linked:false, never an empty array.
    api.get.mockResolvedValue({
      data: {
        methods: [
          { kind: 'password', linked: false, removable: false, detail: null },
          { kind: 'google', linked: true, removable: true, detail: 'user@example.test' },
        ],
      },
    })
    api.post.mockResolvedValue({ data: null })
    setPendingSettingsAction({ kind: 'setPassword' })
    setReauthProof('fresh-reauth-proof')

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Continue with confirmed identity' }))

    const newPasswordField = await screen.findByLabelText('New password')
    expect(newPasswordField).toHaveAttribute('minlength', '12')
    expect(newPasswordField).toHaveAttribute('aria-describedby', 'password-set-new-hint')
    fireEvent.change(newPasswordField, { target: { value: 'brand-new-password-12' } })
    fireEvent.click(screen.getByRole('button', { name: 'Set password' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/auth/password/set',
        expect.objectContaining({
          body: { newPassword: 'brand-new-password-12', currentPassword: null, reauthToken: 'fresh-reauth-proof' },
        }),
      ),
    )
    expect(await screen.findByRole('status')).toHaveTextContent('Your password has been set.')
  })

  it('shows the set-password trigger, not a change form, for an unlinked password method', async () => {
    api.get.mockResolvedValue({
      data: {
        methods: [
          { kind: 'password', linked: false, removable: false, detail: null },
          { kind: 'google', linked: true, removable: true, detail: 'user@example.test' },
        ],
      },
    })

    renderPage()

    expect(await screen.findByRole('button', { name: 'Set a password' })).toBeVisible()
    expect(screen.queryByLabelText('New password')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Your current password')).not.toBeInTheDocument()
  })

  it('blocks a short new password on the change form app-side, without ever POSTing_D184', async () => {
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] },
    })

    renderPage()

    fireEvent.change(await screen.findByLabelText('Your current password'), { target: { value: 'old-correct-password' } })
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'short' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Password must be at least 12 characters.')
    expect(api.post).not.toHaveBeenCalled()
  })

  it('blocks a missing current password on the change form app-side, without ever POSTing_D184', async () => {
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] },
    })

    renderPage()

    fireEvent.change(await screen.findByLabelText('New password'), { target: { value: 'brand-new-password-12' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Enter your current password.')
    expect(api.post).not.toHaveBeenCalled()
  })

  it('blocks a short new password on the set-password form app-side, without ever POSTing_D184', async () => {
    api.get.mockResolvedValue({
      data: {
        methods: [
          { kind: 'password', linked: false, removable: false, detail: null },
          { kind: 'google', linked: true, removable: true, detail: 'user@example.test' },
        ],
      },
    })
    setPendingSettingsAction({ kind: 'setPassword' })
    setReauthProof('fresh-reauth-proof')

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Continue with confirmed identity' }))
    fireEvent.change(await screen.findByLabelText('New password'), { target: { value: 'short' } })
    fireEvent.click(screen.getByRole('button', { name: 'Set password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Password must be at least 12 characters.')
    expect(api.post).not.toHaveBeenCalled()
  })

  it('still surfaces the localized too-short copy from the server auth_validation backstop_D184', async () => {
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'password', linked: true, removable: true, detail: 'Password' }] },
    })
    api.post.mockRejectedValue(new api.ApiError(400, 'auth_validation'))

    renderPage()

    fireEvent.change(await screen.findByLabelText('Your current password'), { target: { value: 'old-correct-password' } })
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'brand-new-password-12' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    await waitFor(() => expect(api.post).toHaveBeenCalled())
    expect(await screen.findByRole('alert')).toHaveTextContent('Password must be at least 12 characters.')
  })

  it('renders localized auth-method names in pt-BR, not the raw API kind_D184', async () => {
    await i18n.changeLanguage('pt-BR')
    api.get.mockResolvedValue({
      data: { methods: [{ kind: 'magicLink', linked: true, removable: true, detail: null }] },
    })

    renderPage()

    const entry = await screen.findByText('Link mágico')
    expect(entry).toBeInTheDocument()
    expect(entry.textContent).not.toContain('magicLink')

    const unlinkButton = screen.getByRole('button', { name: 'Desvincular Link mágico' })
    expect(unlinkButton).toBeInTheDocument()
    expect(unlinkButton.textContent).not.toContain('magicLink')
  })
})
