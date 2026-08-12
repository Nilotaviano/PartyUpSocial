import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { CONTACT_EMAIL } from '../features/legal/legalLocale'

const linkClasses =
  'inline-flex min-h-11 items-center rounded-control px-2 py-2 text-ink-soft underline decoration-from-font underline-offset-4 hover:text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

/**
 * D-072's public surfacing (IMPLEMENTATION_PLAN.md:146): the deletion-request
 * route's audience is logged-out users, so this footer renders on the
 * unauthenticated surfaces as well as the authenticated shell, and carries the
 * §8.6 support contact alongside the three legal documents.
 */
export function AppFooter() {
  const { t } = useTranslation('legal')

  return (
    <footer className="mt-12 border-t-2 border-ink bg-card">
      <nav
        aria-label={t('footer.label')}
        className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-4 gap-y-1 px-4 py-4 sm:px-8"
      >
        <Link className={linkClasses} to="/privacy">
          {t('footer.privacy')}
        </Link>
        <Link className={linkClasses} to="/terms">
          {t('footer.terms')}
        </Link>
        <Link className={linkClasses} to="/community-standards">
          {t('footer.communityStandards')}
        </Link>
        <Link className={linkClasses} to="/account-deletion">
          {t('footer.accountDeletion')}
        </Link>
        <a className={linkClasses} href={`mailto:${CONTACT_EMAIL}`}>
          {t('footer.contact')}
        </a>
      </nav>
    </footer>
  )
}
