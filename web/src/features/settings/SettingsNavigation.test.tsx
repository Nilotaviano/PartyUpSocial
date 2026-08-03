import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import i18n from '../../i18n'
import { SettingsNavigation } from './BlockedUsersPage'

function Location() {
  const location = useLocation()
  return <output data-testid="location">{location.pathname}</output>
}

function renderNavigation(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route
          path="*"
          element={
            <>
              <SettingsNavigation />
              <Location />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  )
}

afterEach(async () => {
  cleanup()
  await i18n.changeLanguage('en')
})

describe('SettingsNavigationTests', () => {
  it('every settings surface reaches Blocked users and exposes current state', () => {
    renderNavigation('/settings/profile')

    expect(screen.getByRole('link', { name: 'Profile' })).toHaveAttribute(
      'aria-current',
      'page',
    )
    const blocked = screen.getByRole('link', { name: 'Blocked users' })
    blocked.focus()
    expect(blocked).toHaveFocus()
    fireEvent.click(blocked)
    expect(screen.getByTestId('location')).toHaveTextContent(
      '/settings/blocked',
    )
    expect(blocked).toHaveAttribute('aria-current', 'page')
  })

  it.each([
    ['en', 'Settings', 'Blocked users'],
    ['pt-BR', 'Configurações', 'Usuários bloqueados'],
    ['es', 'Configuración', 'Usuarios bloqueados'],
  ])(
    'renders localized accessible labels for %s',
    async (locale, navLabel, blockedLabel) => {
      await i18n.changeLanguage(locale)
      renderNavigation('/settings/blocked')

      expect(
        screen.getByRole('navigation', { name: navLabel }),
      ).toBeInTheDocument()
      expect(screen.getByRole('link', { name: blockedLabel })).toHaveAttribute(
        'aria-current',
        'page',
      )
    },
  )
})
