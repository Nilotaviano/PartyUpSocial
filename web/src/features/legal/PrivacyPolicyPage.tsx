import type { ComponentType } from 'react'
import { useTranslation } from 'react-i18next'
import { LegalDocumentPage } from './LegalDocumentPage'
import { resolveLegalLocale, type LegalLocale } from './legalLocale'
import { PrivacyEn, PrivacyEs, PrivacyPtBR } from './prose/privacy'

const prose: Record<LegalLocale, ComponentType> = {
  en: PrivacyEn,
  'pt-BR': PrivacyPtBR,
  es: PrivacyEs,
}

export function PrivacyPolicyPage() {
  const { t, i18n } = useTranslation('legal')
  const Prose = prose[resolveLegalLocale(i18n.language)]

  return (
    <LegalDocumentPage testId="screen-privacy" title={t('privacy.title')}>
      <Prose />
    </LegalDocumentPage>
  )
}
