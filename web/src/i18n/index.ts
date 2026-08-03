import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import LanguageDetector from 'i18next-browser-languagedetector'

import enAuth from './en/auth.json'
import enCommon from './en/common.json'
import enErrors from './en/errors.json'
import enFriends from './en/friends.json'
import enOnboarding from './en/onboarding.json'
import enProfile from './en/profile.json'
import enSettings from './en/settings.json'
import esAuth from './es/auth.json'
import esCommon from './es/common.json'
import esErrors from './es/errors.json'
import esFriends from './es/friends.json'
import esOnboarding from './es/onboarding.json'
import esProfile from './es/profile.json'
import esSettings from './es/settings.json'
import ptBRAuth from './pt-BR/auth.json'
import ptBRCommon from './pt-BR/common.json'
import ptBRErrors from './pt-BR/errors.json'
import ptBRFriends from './pt-BR/friends.json'
import ptBROnboarding from './pt-BR/onboarding.json'
import ptBRProfile from './pt-BR/profile.json'
import ptBRSettings from './pt-BR/settings.json'

export const defaultNS = 'common'
export const resources = {
  en: {
    common: enCommon,
    auth: enAuth,
    onboarding: enOnboarding,
    profile: enProfile,
    settings: enSettings,
    errors: enErrors,
    friends: enFriends,
  },
  'pt-BR': {
    common: ptBRCommon,
    auth: ptBRAuth,
    onboarding: ptBROnboarding,
    profile: ptBRProfile,
    settings: ptBRSettings,
    errors: ptBRErrors,
    friends: ptBRFriends,
  },
  es: {
    common: esCommon,
    auth: esAuth,
    onboarding: esOnboarding,
    profile: esProfile,
    settings: esSettings,
    errors: esErrors,
    friends: esFriends,
  },
}

void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    fallbackLng: 'en',
    supportedLngs: ['en', 'pt-BR', 'es'],
    defaultNS,
    ns: [
      'common',
      'auth',
      'onboarding',
      'profile',
      'settings',
      'errors',
      'friends',
    ],
    resources,
    interpolation: {
      escapeValue: false,
    },
  })

export default i18n
