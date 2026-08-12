import type { ComponentType } from 'react'
import { useTranslation } from 'react-i18next'
import { LegalDocumentPage } from './LegalDocumentPage'
import { resolveLegalLocale, type LegalLocale } from './legalLocale'
import {
  CommunityStandardsEn,
  CommunityStandardsEs,
  CommunityStandardsPtBR,
} from './prose/communityStandards'

const prose: Record<LegalLocale, ComponentType> = {
  en: CommunityStandardsEn,
  'pt-BR': CommunityStandardsPtBR,
  es: CommunityStandardsEs,
}

export function CommunityStandardsPage() {
  const { t, i18n } = useTranslation('legal')
  const Prose = prose[resolveLegalLocale(i18n.language)]

  return (
    <LegalDocumentPage
      testId="screen-community-standards"
      title={t('communityStandards.title')}
    >
      <Prose />
    </LegalDocumentPage>
  )
}
