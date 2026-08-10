import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import LanguageDetector from 'i18next-browser-languagedetector'

import enAuth from './en/auth.json'
import enAvailability from './en/availability.json'
import enCommon from './en/common.json'
import enErrors from './en/errors.json'
import enFriends from './en/friends.json'
import enHome from './en/home.json'
import enNotifications from './en/notifications.json'
import enOnboarding from './en/onboarding.json'
import enPolls from './en/polls.json'
import enProfile from './en/profile.json'
import enSessions from './en/sessions.json'
import enSettings from './en/settings.json'
import enTour from './en/tour.json'
import esAuth from './es/auth.json'
import esAvailability from './es/availability.json'
import esCommon from './es/common.json'
import esErrors from './es/errors.json'
import esFriends from './es/friends.json'
import esHome from './es/home.json'
import esNotifications from './es/notifications.json'
import esOnboarding from './es/onboarding.json'
import esPolls from './es/polls.json'
import esProfile from './es/profile.json'
import esSessions from './es/sessions.json'
import esSettings from './es/settings.json'
import esTour from './es/tour.json'
import ptBRAuth from './pt-BR/auth.json'
import ptBRAvailability from './pt-BR/availability.json'
import ptBRCommon from './pt-BR/common.json'
import ptBRErrors from './pt-BR/errors.json'
import ptBRFriends from './pt-BR/friends.json'
import ptBRHome from './pt-BR/home.json'
import ptBRNotifications from './pt-BR/notifications.json'
import ptBROnboarding from './pt-BR/onboarding.json'
import ptBRPolls from './pt-BR/polls.json'
import ptBRProfile from './pt-BR/profile.json'
import ptBRSessions from './pt-BR/sessions.json'
import ptBRSettings from './pt-BR/settings.json'
import ptBRTour from './pt-BR/tour.json'

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
    sessions: enSessions,
    availability: enAvailability,
    home: enHome,
    polls: enPolls,
    notifications: enNotifications,
    tour: enTour,
  },
  'pt-BR': {
    common: ptBRCommon,
    auth: ptBRAuth,
    onboarding: ptBROnboarding,
    profile: ptBRProfile,
    settings: ptBRSettings,
    errors: ptBRErrors,
    friends: ptBRFriends,
    sessions: ptBRSessions,
    availability: ptBRAvailability,
    home: ptBRHome,
    polls: ptBRPolls,
    notifications: ptBRNotifications,
    tour: ptBRTour,
  },
  es: {
    common: esCommon,
    auth: esAuth,
    onboarding: esOnboarding,
    profile: esProfile,
    settings: esSettings,
    errors: esErrors,
    friends: esFriends,
    sessions: esSessions,
    availability: esAvailability,
    home: esHome,
    polls: esPolls,
    notifications: esNotifications,
    tour: esTour,
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
      'sessions',
      'availability',
      'home',
      'polls',
      'notifications',
      'tour',
    ],
    resources,
    interpolation: {
      escapeValue: false,
    },
  })

export default i18n
