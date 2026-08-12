import type { ComponentType } from 'react'
import { useTranslation } from 'react-i18next'
import { LegalDocumentPage } from './LegalDocumentPage'
import { resolveLegalLocale, type LegalLocale } from './legalLocale'
import { TermsEn, TermsEs, TermsPtBR } from './prose/terms'

const prose: Record<LegalLocale, ComponentType> = {
  en: TermsEn,
  'pt-BR': TermsPtBR,
  es: TermsEs,
}

export function TermsPage() {
  const { t, i18n } = useTranslation('legal')
  const Prose = prose[resolveLegalLocale(i18n.language)]

  return (
    <LegalDocumentPage testId="screen-terms" title={t('terms.title')}>
      <Prose />
    </LegalDocumentPage>
  )
}
