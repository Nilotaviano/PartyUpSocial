import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { CONTACT_EMAIL } from '../features/legal/legalLocale'
import { BrandMark } from './ui/brandMark'

// Prototype grammar uses h-8 link targets; kept at min-h-11 here (touch
// target floor) rather than the prototype's literal value.
const linkClasses =
  'inline-flex min-h-11 items-center text-[12px] text-muted-foreground underline decoration-from-font underline-offset-4 hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

/**
 * D-072's public surfacing (IMPLEMENTATION_PLAN.md:146): the deletion-request
 * route's audience is logged-out users, so this footer renders on the
 * unauthenticated surfaces as well as the authenticated shell, and carries the
 * §8.6 support contact alongside the three legal documents.
 */
export function AppFooter() {
  const { t } = useTranslation('legal')

  return (
    <footer className="mt-10 border-t border-border">
      <div className="mx-auto flex max-w-[1400px] flex-col gap-3 px-4 py-6 sm:flex-row sm:items-center sm:justify-between sm:px-8">
        <p className="flex items-center gap-2 font-display text-[12px] font-bold text-muted-foreground">
          <BrandMark className="size-4" />
          {t('footer.tagline')}
        </p>
        <nav
          aria-label={t('footer.label')}
          className="flex flex-wrap items-center gap-x-4 gap-y-1"
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
      </div>
    </footer>
  )
}
