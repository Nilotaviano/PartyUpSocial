import { useMemo, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { LEGAL_UPDATED_AT } from './legalLocale'

/**
 * Shared chrome for the three legal documents (PHASE8_DESIGN.md §3 S5): the
 * title, the draft banner every document carries until the owner signs the
 * nine documents off, the last-updated line, and the prose slot. Every string
 * here is i18next; the prose itself is the translation artifact and lives in
 * the locale-selected components under `prose/`.
 */
export function LegalDocumentPage({
  testId,
  title,
  children,
}: {
  testId: string
  title: string
  children: ReactNode
}) {
  const { t, i18n } = useTranslation('legal')
  const updatedAt = useMemo(
    () =>
      // LEGAL_UPDATED_AT is a calendar date, not an instant: without an explicit UTC zone,
      // formatting shifts it a day early for every viewer west of UTC.
      new Intl.DateTimeFormat(i18n.language, { dateStyle: 'long', timeZone: 'UTC' }).format(
        new Date(LEGAL_UPDATED_AT),
      ),
    [i18n.language],
  )

  return (
    <main data-testid={testId}>
      <PageScaffold
        className="gap-6"
        width="form"
      >
        <PageHeader
          meta={`${t('updatedLabel')}: ${updatedAt}`}
          title={title}
        />

        <p
          className="rounded-card border-2 border-ink bg-accent p-4 font-display font-semibold text-accent-ink dark:border-accent-ink"
          data-testid="legal-draft-banner"
          role="note"
          aria-label={t('draft.label')}
        >
          {t('draft.banner')}
        </p>

        <div className="flex flex-col gap-8">{children}</div>
      </PageScaffold>
    </main>
  )
}
