import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

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
import i18n from '../../i18n'
import { FeedbackDialog } from './FeedbackDialog'
import { FeedbackLink } from './FeedbackLink'

/**
 * The dialog is controlled, so the harness owns the open state exactly as the real entry points
 * do — a hard-coded `open` would keep the panel mounted through a successful submit and leave the
 * confirmation region `aria-hidden` behind Radix's own modal guard.
 */
function Harness({ route, onOpenChange }: { route: string; onOpenChange: (open: boolean) => void }) {
  const [open, setOpen] = useState(true)
  return (
    <MemoryRouter initialEntries={[route]}>
      <FeedbackDialog
        open={open}
        onOpenChange={(next) => {
          onOpenChange(next)
          setOpen(next)
        }}
      />
    </MemoryRouter>
  )
}

function renderDialog(route = '/polls/abc?tab=votes') {
  const onOpenChange = vi.fn()
  const result = render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}
    >
      <Harness route={route} onOpenChange={onOpenChange} />
    </QueryClientProvider>,
  )
  return { ...result, onOpenChange }
}

function renderLink() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/guest/0198c2f0-0000-7000-8000-000000000001']}>
        <FeedbackLink />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(async () => {
  await i18n.changeLanguage('en')
  api.post.mockResolvedValue({ data: { id: '0198c2f0-0000-7000-8000-0000000000ff' } })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('FeedbackDialog — the D-212 form', () => {
  it('offers exactly the three D-212 types as a keyboard-operable radio group_D212', async () => {
    renderDialog()

    const radios = await screen.findAllByRole('radio')
    expect(radios.map((radio) => radio.getAttribute('value'))).toEqual(['bug', 'idea', 'other'])
    // Native radios: one group, one tab stop, arrow-key roving from the platform.
    expect(new Set(radios.map((radio) => radio.getAttribute('name'))).size).toBe(1)
    expect(screen.getByRole('radio', { name: 'Bug' })).toBeChecked()
  })

  it('labels the message field and caps it at the D-212 limit with a live counter_D212', async () => {
    renderDialog()

    const message = await screen.findByLabelText('Your message')
    expect(message).toHaveAttribute('maxlength', '4000')
    expect(screen.getByText('0 of 4000 characters')).toBeInTheDocument()

    fireEvent.change(message, { target: { value: 'crash on vote' } })
    expect(screen.getByText('13 of 4000 characters')).toBeInTheDocument()
  })

  it('refuses an empty message without touching the network_D212', async () => {
    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: 'Send' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Write a message before sending.')
    expect(api.post).not.toHaveBeenCalled()
  })

  it('clears the empty-message error as soon as the user starts typing_D212', async () => {
    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: 'Send' }))
    const message = screen.getByLabelText('Your message')
    expect(await screen.findByRole('alert')).toHaveTextContent('Write a message before sending.')
    expect(message).toHaveAttribute('aria-invalid', 'true')

    fireEvent.change(message, { target: { value: 'crash on vote' } })

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(message).not.toHaveAttribute('aria-invalid')
  })
})

describe('FeedbackDialog — submission and auto-attached context', () => {
  it('posts the chosen type, the message and the D-212 auto-context_D212_D217', async () => {
    const { onOpenChange } = renderDialog('/polls/abc?tab=votes')

    fireEvent.click(await screen.findByRole('radio', { name: 'Idea' }))
    fireEvent.change(screen.getByLabelText('Your message'), {
      target: { value: '  let me pin a favourite game  ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    const [path, options] = api.post.mock.calls[0] as [
      string,
      { params: { header: Record<string, string> }; body: Record<string, unknown> },
    ]
    expect(path).toBe('/api/v1/feedback')
    expect(options.params.header).toEqual({ 'X-CSRF-TOKEN': '' })
    expect(options.body.type).toBe('idea')
    expect(options.body.message).toBe('let me pin a favourite game')
    expect(options.body.route).toBe('/polls/abc?tab=votes')
    expect(options.body.locale).toBe('en')
    expect(options.body.userAgent).toBe(navigator.userAgent)
    expect(options.body.appVersion).toBe(__APP_VERSION__)

    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false))
  })

  it('announces the confirmation from a region that survives the dialog closing_D217', async () => {
    renderDialog()

    fireEvent.change(await screen.findByLabelText('Your message'), {
      target: { value: 'the week grid scrolls oddly' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    const status = await screen.findByRole('status')
    await waitFor(() => expect(status).toHaveTextContent('Thanks — your feedback was sent.'))
  })

  it('renders the D-213 rate-limit copy on a 429, not the generic failure line_D213', async () => {
    api.post.mockRejectedValue(new TestApiError(429, 'rate_limited'))
    renderDialog()

    fireEvent.change(await screen.findByLabelText('Your message'), {
      target: { value: 'again' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Slow down — you have sent several notes already. Try again in a few minutes.',
    )
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('renders the generic failure line on any other error_D212', async () => {
    api.post.mockRejectedValue(new TestApiError(400, 'invalid_request'))
    renderDialog()

    fireEvent.change(await screen.findByLabelText('Your message'), { target: { value: 'x' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The feedback could not be sent. Try again.',
    )
  })
})

describe('FeedbackDialog — localization D-217', () => {
  it('renders the dialog in pt-BR when that is the active locale_D217', async () => {
    await i18n.changeLanguage('pt-BR')
    renderDialog()

    expect(await screen.findByRole('heading', { name: 'Enviar feedback' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Erro' })).toBeInTheDocument()
  })

  it('sends the active UI locale as the attached locale, not the browser default_D212', async () => {
    await i18n.changeLanguage('es')
    renderDialog()

    fireEvent.change(await screen.findByLabelText('Tu mensaje'), { target: { value: 'hola' } })
    fireEvent.click(screen.getByRole('button', { name: 'Enviar' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    const [, options] = api.post.mock.calls[0] as [string, { body: { locale: string } }]
    expect(options.body.locale).toBe('es')
  })
})

describe('FeedbackLink — the anonymous entry point D-217', () => {
  it('opens the dialog from a discreet button and returns focus to it on close_D217', async () => {
    renderLink()

    const trigger = screen.getByRole('button', { name: 'Send feedback' })
    trigger.focus()
    fireEvent.click(trigger)

    expect(await screen.findByRole('dialog')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(trigger).toHaveFocus())
  })

  it('closes on Escape without submitting anything_D217', async () => {
    renderLink()

    fireEvent.click(screen.getByRole('button', { name: 'Send feedback' }))
    await screen.findByRole('dialog')

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(api.post).not.toHaveBeenCalled()
  })
})
