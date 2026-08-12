import { useEffect, useRef, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

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
  const { t } = useTranslation('legal')
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [])

  return (
    <main
      className="mx-auto max-w-3xl space-y-6 p-4 sm:p-8"
      data-testid={testId}
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-display font-display font-bold text-ink focus:outline-none"
      >
        {title}
      </h1>

      <p
        className="rounded-card border-2 border-ink bg-accent p-4 font-display font-semibold text-accent-ink"
        data-testid="legal-draft-banner"
        role="note"
        aria-label={t('draft.label')}
      >
        {t('draft.banner')}
      </p>

      <p className="text-ink-soft">
        {t('updatedLabel')}: {t('updated')}
      </p>

      <div className="space-y-8">{children}</div>
    </main>
  )
}
