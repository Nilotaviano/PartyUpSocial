import { cleanup, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import i18n from '../../i18n'
import { AppFooter } from '../../components/AppFooter'
import { CommunityStandardsPage } from './CommunityStandardsPage'
import { PrivacyPolicyPage } from './PrivacyPolicyPage'
import { TermsPage } from './TermsPage'
import { resolveLegalLocale } from './legalLocale'

const locales = ['en', 'pt-BR', 'es'] as const

function renderWithRouter(element: React.ReactElement) {
  return render(<MemoryRouter>{element}</MemoryRouter>)
}

beforeEach(async () => {
  await i18n.changeLanguage('en')
})

afterEach(() => {
  cleanup()
})

describe('legal document locale selection', () => {
  it('maps every i18next tag shape onto one of the three prose variants', () => {
    expect(resolveLegalLocale('en')).toBe('en')
    expect(resolveLegalLocale('en-GB')).toBe('en')
    expect(resolveLegalLocale('pt')).toBe('pt-BR')
    expect(resolveLegalLocale('pt-BR')).toBe('pt-BR')
    expect(resolveLegalLocale('pt-PT')).toBe('pt-BR')
    expect(resolveLegalLocale('es')).toBe('es')
    expect(resolveLegalLocale('es-419')).toBe('es')
    expect(resolveLegalLocale('de')).toBe('en')
    expect(resolveLegalLocale(undefined)).toBe('en')
  })
})

describe('privacy policy', () => {
  for (const locale of locales) {
    it(`${locale} renders its own prose behind the localized title`, async () => {
      await i18n.changeLanguage(locale)

      renderWithRouter(<PrivacyPolicyPage />)

      expect(
        screen.getByRole('heading', {
          level: 1,
          name: i18n.t('privacy.title', { ns: 'legal' }),
        }),
      ).toBeTruthy()
      expect(screen.getAllByRole('heading', { level: 2 }).length).toBeGreaterThan(4)
      expect(document.body.textContent).toContain('privacy@partyup.social')
    })
  }

  it('describes the obligations §8.6 requires of it', async () => {
    renderWithRouter(<PrivacyPolicyPage />)

    const text = document.body.textContent ?? ''
    // Q1 (ratified): the last-activity collection, described plainly.
    expect(text).toContain('When you last used PartyUp')
    // D-161's transparency half: derived measures, no third-party analytics.
    expect(text).toContain('derived measures')
    expect(text).toContain('no third-party analytics service')
    // Q6 (ratified): 30-day backups, shared content until deletion.
    expect(text).toContain('kept for 30 days')
    expect(text).toContain('until your account is deleted')
    // D-148 / D-158 / D-165 / D-072.
    expect(text).toContain('seven-day grace period')
    expect(text).toContain('automatically cancels the deletion')
    expect(text).toContain('signs you out on every device')
    expect(text).toContain('/account-deletion')
    // Steam sharing and what unlinking revokes (D-050/D-190).
    expect(text).toContain('Steam Web API')
    expect(text).toContain('Unlinking Steam revokes that access')
    // LGPD rights and the 13+ posture.
    expect(text).toContain('LGPD')
    expect(text).toContain('not directed to children under 13')
  })
})

describe('terms of service', () => {
  for (const locale of locales) {
    it(`${locale} renders its own prose behind the localized title`, async () => {
      await i18n.changeLanguage(locale)

      renderWithRouter(<TermsPage />)

      expect(
        screen.getByRole('heading', {
          level: 1,
          name: i18n.t('terms.title', { ns: 'legal' }),
        }),
      ).toBeTruthy()
    })
  }

  it('states the D-177/D-180 enforcement terms and flags governing law for review', () => {
    renderWithRouter(<TermsPage />)

    const text = document.body.textContent ?? ''
    expect(text).toContain('we may ban it')
    expect(text).toContain('Content removal is permanent')
    expect(text).toContain('does not bring back anything that was removed')
    expect(text).toContain('Pending owner review')
  })
})

describe('community standards', () => {
  for (const locale of locales) {
    it(`${locale} renders its own prose behind the localized title`, async () => {
      await i18n.changeLanguage(locale)

      renderWithRouter(<CommunityStandardsPage />)

      expect(
        screen.getByRole('heading', {
          level: 1,
          name: i18n.t('communityStandards.title', { ns: 'legal' }),
        }),
      ).toBeTruthy()
    })
  }

  it('points reporting at the contact address, not an unbuilt report button', () => {
    renderWithRouter(<CommunityStandardsPage />)

    const text = document.body.textContent ?? ''
    expect(text).toContain('In-app reporting is not built yet')
    expect(text).toContain('privacy@partyup.social')
    expect(text).not.toMatch(/report button/i)
    expect(text).toContain('profanity filter')
    expect(text).toContain('Warning')
  })
})

describe('draft banner', () => {
  const pages = [
    ['privacy', <PrivacyPolicyPage key="privacy" />],
    ['terms', <TermsPage key="terms" />],
    ['community standards', <CommunityStandardsPage key="standards" />],
  ] as const

  for (const [name, element] of pages) {
    it(`${name} ships marked as a draft pending legal review`, () => {
      renderWithRouter(element)

      const banner = screen.getByTestId('legal-draft-banner')
      expect(banner.textContent).toContain('DRAFT')
      expect(banner.textContent).toContain('pending legal review')
      expect(screen.getByText(/Last updated/)).toBeTruthy()
    })
  }

  it('marks every locale variant as a draft too', async () => {
    for (const locale of ['pt-BR', 'es'] as const) {
      await i18n.changeLanguage(locale)
      const view = renderWithRouter(<PrivacyPolicyPage />)
      expect(
        view.getByTestId('legal-draft-banner').textContent?.length ?? 0,
      ).toBeGreaterThan(0)
      view.unmount()
    }
  })
})

describe('app footer', () => {
  it('publishes the public deletion route to logged-out users_D072', () => {
    renderWithRouter(<AppFooter />)

    const nav = screen.getByRole('navigation', { name: 'Legal and support' })
    const deletion = within(nav).getByRole('link', {
      name: 'Delete your account',
    })
    expect(deletion.getAttribute('href')).toBe('/account-deletion')
  })

  it('links the three legal documents and the support contact', () => {
    renderWithRouter(<AppFooter />)

    const nav = screen.getByRole('navigation', { name: 'Legal and support' })
    const hrefs = within(nav)
      .getAllByRole('link')
      .map((link) => link.getAttribute('href'))
    expect(hrefs).toEqual([
      '/privacy',
      '/terms',
      '/community-standards',
      '/account-deletion',
      'mailto:privacy@partyup.social',
    ])
  })

  for (const locale of locales) {
    it(`${locale} labels every footer link`, async () => {
      await i18n.changeLanguage(locale)

      renderWithRouter(<AppFooter />)

      const nav = screen.getByRole('navigation', {
        name: i18n.t('footer.label', { ns: 'legal' }),
      })
      for (const link of within(nav).getAllByRole('link')) {
        expect(link.textContent?.trim().length ?? 0).toBeGreaterThan(0)
      }
    })
  }
})
