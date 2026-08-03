import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

vi.mock('react-qr-code', () => ({
  default: ({ value }: { value: string }) => (
    <svg aria-hidden="true" data-testid="qr-code" data-value={value} />
  ),
}))

import '../../i18n'
import { ShareCard } from './ShareCard'

function renderShare() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <ShareCard />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockResolvedValue({
    data: {
      friendCode: '23456789AB',
      addUrl: 'https://links.partyup.test/add/23456789AB',
      profileUrl: 'https://links.partyup.test/u/partyup',
    },
  })
  Object.defineProperty(navigator, 'clipboard', {
    configurable: true,
    value: { writeText: vi.fn().mockResolvedValue(undefined) },
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('ShareCard', () => {
  it('encodes the API-owned absolute add URL and exposes selectable text_D144', async () => {
    renderShare()

    const qr = await screen.findByTestId('qr-code')
    expect(qr).toHaveAttribute(
      'data-value',
      'https://links.partyup.test/add/23456789AB',
    )
    expect(qr).toHaveAttribute('aria-hidden', 'true')
    expect(
      screen.getByRole('link', {
        name: 'https://links.partyup.test/add/23456789AB',
      }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('link', {
        name: 'https://links.partyup.test/u/partyup',
      }),
    ).toBeInTheDocument()
  })

  it('copies only API-returned values and announces success_D139', async () => {
    renderShare()
    fireEvent.click(
      await screen.findByRole('button', { name: 'Copy add link' }),
    )

    await waitFor(() =>
      expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
        'https://links.partyup.test/add/23456789AB',
      ),
    )
    expect(await screen.findByText('Copy add link copied.')).toBeInTheDocument()
  })
})
