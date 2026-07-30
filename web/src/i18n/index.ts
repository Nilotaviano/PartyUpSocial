import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import LanguageDetector from 'i18next-browser-languagedetector'

import en from './en/common.json'
import ptBR from './pt-BR/common.json'
import es from './es/common.json'

export const defaultNS = 'common'

void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    fallbackLng: 'en',
    supportedLngs: ['en', 'pt-BR', 'es'],
    defaultNS,
    resources: {
      en: { common: en },
      'pt-BR': { common: ptBR },
      es: { common: es },
    },
    interpolation: {
      escapeValue: false,
    },
  })

export default i18n
