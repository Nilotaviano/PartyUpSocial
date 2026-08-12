import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

import '../../i18n'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { SteamGamePicker, type SteamGamePickerResult } from './SteamGamePicker'

function Harness() {
  const { t } = useTranslation('sessions')
  const [name, setName] = useState('')
  const [appId, setAppId] = useState('')
  const [selected, setSelected] = useState<SteamGamePickerResult | null>(null)

  return (
    <>
      <SteamGamePicker
        appIdId="field-game-app-id"
        appIdValue={appId}
        nameId="field-game-name"
        nameValue={name}
        t={t}
        onAppIdChange={setAppId}
        onNameChange={setName}
        onSelect={(result) => {
          setSelected(result)
          setName(result.name)
          setAppId(result.appId)
        }}
      />
      <p data-testid="selected">{selected ? JSON.stringify(selected) : 'none'}</p>
    </>
  )
}

function renderPicker() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <Harness />
    </QueryClientProvider>,
  )
}

const results = [
  { appId: '730', name: 'Counter-Strike 2' },
  { appId: '1091500', name: 'Cyberpunk 2077' },
]

beforeEach(() => {
  api.get.mockResolvedValue({
    data: { catalogAvailable: true, results },
  })
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
  vi.useRealTimers()
})

describe('SteamGamePicker_D194', () => {
  it('debounces the search 300ms and only queries once the term settles_D194', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    renderPicker()

    const input = screen.getByRole('combobox')
    fireEvent.change(input, { target: { value: 'co' } })
    fireEvent.change(input, { target: { value: 'cou' } })

    await vi.advanceTimersByTimeAsync(100)
    expect(api.get).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(250)
    await vi.waitFor(() => expect(api.get).toHaveBeenCalledTimes(1))
    expect(api.get).toHaveBeenCalledWith(
      '/api/v1/steam-catalog',
      expect.objectContaining({ params: { query: { query: 'cou' } } }),
    )
  })

  it('renders search results as listbox options and fills name+appId on select_D194', async () => {
    renderPicker()

    const input = screen.getByRole('combobox')
    fireEvent.change(input, { target: { value: 'counter' } })

    const option = await screen.findByRole('option', { name: 'Counter-Strike 2' })
    fireEvent.mouseDown(option)

    await waitFor(() =>
      expect(screen.getByTestId('selected')).toHaveTextContent(
        JSON.stringify({ appId: '730', name: 'Counter-Strike 2' }),
      ),
    )
    expect(input).toHaveValue('Counter-Strike 2')
  })

  it('navigates options with arrow keys and selects the active option with Enter_D194', async () => {
    renderPicker()

    const input = screen.getByRole('combobox')
    fireEvent.change(input, { target: { value: 'counter' } })
    await screen.findByRole('option', { name: 'Counter-Strike 2' })

    fireEvent.keyDown(input, { key: 'ArrowDown' })
    fireEvent.keyDown(input, { key: 'ArrowDown' })
    fireEvent.keyDown(input, { key: 'Enter' })

    await waitFor(() =>
      expect(screen.getByTestId('selected')).toHaveTextContent(
        JSON.stringify({ appId: '1091500', name: 'Cyberpunk 2077' }),
      ),
    )
  })

  it('closes the popup on Escape without clearing the typed text_D194', async () => {
    renderPicker()

    const input = screen.getByRole('combobox')
    fireEvent.change(input, { target: { value: 'counter' } })
    await screen.findByRole('listbox')

    fireEvent.keyDown(input, { key: 'Escape' })

    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(input).toHaveValue('counter')
  })

  it('typing without selecting stays legal free text (no appId filled)_D194', async () => {
    renderPicker()

    const input = screen.getByRole('combobox')
    fireEvent.change(input, { target: { value: 'a homebrew game' } })

    await waitFor(() => expect(api.get).toHaveBeenCalled())
    expect(screen.getByTestId('selected')).toHaveTextContent('none')
    expect(input).toHaveValue('a homebrew game')
  })

  it('degrades to the manual name+appId pair when catalogAvailable is false_D194', async () => {
    api.get.mockResolvedValue({ data: { catalogAvailable: false, results: [] } })
    renderPicker()

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'counter' } })

    expect(await screen.findByLabelText('Enter a game name')).toBeInTheDocument()
    expect(screen.getByLabelText('Steam app ID')).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('degrades to the manual pair when the search request errors_D194', async () => {
    api.get.mockRejectedValue(new Error('network down'))
    renderPicker()

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'counter' } })

    expect(await screen.findByLabelText('Enter a game name')).toBeInTheDocument()
    expect(screen.getByLabelText('Steam app ID')).toBeInTheDocument()
  })
})
