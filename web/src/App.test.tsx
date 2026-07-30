import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router'
import App from './App'
import './i18n'

function renderApp() {
  const queryClient = new QueryClient()

  return render(
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>,
  )
}

describe('App', () => {
  it('renders the app name', () => {
    renderApp()

    expect(screen.getByText('PartyUp')).toBeInTheDocument()
  })
})
