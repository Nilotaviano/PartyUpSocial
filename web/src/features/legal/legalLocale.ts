export const LEGAL_LOCALES = ['en', 'pt-BR', 'es'] as const

export type LegalLocale = (typeof LEGAL_LOCALES)[number]

/**
 * Picks the prose variant for a document. i18next hands us tags like `pt`,
 * `pt-BR`, `pt-PT`, `es-419` or `en-GB`, so the match is on the primary
 * subtag, with English as the fallback (matching i18n's own `fallbackLng`).
 */
export function resolveLegalLocale(language: string | undefined): LegalLocale {
  const primary = (language ?? '').toLowerCase().split('-')[0]

  if (primary === 'pt') {
    return 'pt-BR'
  }

  if (primary === 'es') {
    return 'es'
  }

  return 'en'
}

export const CONTACT_EMAIL = 'privacy@mail.partyup.social'

export const LEGAL_UPDATED_AT = '2026-08-12'
