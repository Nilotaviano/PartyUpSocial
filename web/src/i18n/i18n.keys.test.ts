import { describe, expect, it } from 'vitest'
import enAuth from './en/auth.json'
import enCommon from './en/common.json'
import enErrors from './en/errors.json'
import enOnboarding from './en/onboarding.json'
import enProfile from './en/profile.json'
import enSettings from './en/settings.json'
import esAuth from './es/auth.json'
import esCommon from './es/common.json'
import esErrors from './es/errors.json'
import esOnboarding from './es/onboarding.json'
import esProfile from './es/profile.json'
import esSettings from './es/settings.json'
import ptBRAuth from './pt-BR/auth.json'
import ptBRCommon from './pt-BR/common.json'
import ptBRErrors from './pt-BR/errors.json'
import ptBROnboarding from './pt-BR/onboarding.json'
import ptBRProfile from './pt-BR/profile.json'
import ptBRSettings from './pt-BR/settings.json'

type Resources = Record<string, unknown>

const locales: Record<string, Record<string, Resources>> = {
  en: { common: enCommon, auth: enAuth, onboarding: enOnboarding, profile: enProfile, settings: enSettings, errors: enErrors },
  'pt-BR': { common: ptBRCommon, auth: ptBRAuth, onboarding: ptBROnboarding, profile: ptBRProfile, settings: ptBRSettings, errors: ptBRErrors },
  es: { common: esCommon, auth: esAuth, onboarding: esOnboarding, profile: esProfile, settings: esSettings, errors: esErrors },
}

function deepKeys(value: Resources, prefix = ''): string[] {
  return Object.entries(value).flatMap(([key, nested]) => {
    const path = prefix ? `${prefix}.${key}` : key
    return nested && typeof nested === 'object' && !Array.isArray(nested)
      ? deepKeys(nested as Resources, path)
      : [path]
  })
}

describe('i18n locale keys D-060', () => {
  for (const locale of ['pt-BR', 'es']) {
    it(`${locale} has the complete English key set`, () => {
      for (const namespace of Object.keys(locales.en)) {
        expect(deepKeys(locales[locale][namespace]).sort()).toEqual(deepKeys(locales.en[namespace]).sort())
      }
    })
  }
})
