import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, DELETE: api.delete },
  isApiError: () => false,
}))

import '../../i18n'
import { AddFriendPage } from './AddFriendPage'

function renderAdd(path: string) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/add/:code" element={<AddFriendPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AddFriendPage', () => {
  it('normalizes spaces, dashes, and case before resolving_D144', async () => {
    api.get.mockResolvedValue({
      data: {
        profile: {
          disclosure: 'username_only',
          username: 'friend.one',
          relationship: { kind: 'none' },
        },
      },
    })

    renderAdd('/add/2345-6789-ab')

    expect(await screen.findByText('@friend.one')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1 })).toHaveFocus()
    expect(screen.getByLabelText('Friend code')).toHaveValue('23456789AB')
    expect(api.get).toHaveBeenCalledWith('/api/v1/friend-codes/{friendCode}', {
      params: { path: { friendCode: '23456789AB' } },
    })
    expect(api.post).not.toHaveBeenCalled()
  })

  it('keeps the entered code after a failed resolution_D144', async () => {
    api.get.mockRejectedValue(new Error('missing'))
    renderAdd('/add/23456789AB')

    await screen.findByRole('alert')
    expect(screen.getByLabelText('Friend code')).toHaveValue('23456789AB')
  })

  it('rejects invalid input locally without making a lookup_D144', () => {
    renderAdd('/add/invalid')

    const input = screen.getByLabelText('Friend code')
    fireEvent.change(input, { target: { value: '2345 6789AB' } })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Find friend' }).closest('form')!,
    )

    expect(screen.getAllByRole('alert').length).toBeGreaterThan(0)
    expect(api.get).not.toHaveBeenCalled()
  })
})
