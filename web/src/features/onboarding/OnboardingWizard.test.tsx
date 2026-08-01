import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
vi.mock('../../auth/useSession', () => ({ useCompleteOnboardingMutation: () => ({ isPending: false, mutateAsync: vi.fn(), error: null }) }))
import '../../i18n'
import { OnboardingWizard } from './OnboardingWizard'
describe('OnboardingWizard', () => {
  it('starts in D-127 order and cannot skip steps_D127', async () => {
    sessionStorage.clear()
    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={['/onboarding/friends']}><OnboardingWizard /></MemoryRouter></QueryClientProvider>)
    await waitFor(() => expect(screen.getByRole('heading')).toHaveTextContent('Choose a username'))
    expect(screen.getByText('Step 1 of 6: Choose a username')).toBeInTheDocument()
  })
})