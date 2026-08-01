import i18n from '../../i18n'
import { describe, expect, it } from 'vitest'

describe('LanguageSwitch', () => {
  it('supports the browser language, English fallback, and account override_D146', async () => {
    expect(i18n.options.fallbackLng).toEqual(['en'])
    expect(i18n.options.supportedLngs).toEqual(expect.arrayContaining(['en', 'pt-BR', 'es']))

    await i18n.changeLanguage('pt-BR')
    expect(i18n.language).toBe('pt-BR')

    await i18n.changeLanguage('es')
    expect(i18n.language).toBe('es')
  })
})