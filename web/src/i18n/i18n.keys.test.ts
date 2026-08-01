import { describe, expect, it } from 'vitest'
import { resources } from './index'

type Resources = Record<string, unknown>

const locales = ['en', 'pt-BR', 'es'] as const
const namespaces = ['common', 'auth', 'onboarding', 'profile', 'settings', 'errors'] as const
const registeredResources = resources as Record<string, Record<string, Resources>>

function deepKeys(value: Resources, prefix = ''): string[] {
  return Object.entries(value).flatMap(([key, nested]) => {
    const path = prefix ? `${prefix}.${key}` : key
    return nested && typeof nested === 'object' && !Array.isArray(nested)
      ? deepKeys(nested as Resources, path)
      : [path]
  })
}

describe('i18n locale keys D-060', () => {
  it('registers every locale and namespace at runtime', () => {
    for (const locale of locales) {
      for (const namespace of namespaces) {
        expect(registeredResources[locale][namespace]).toBeDefined()
      }
    }
  })

  for (const locale of ['pt-BR', 'es'] as const) {
    it(`${locale} has the complete English key set`, () => {
      for (const namespace of namespaces) {
        expect(deepKeys(registeredResources[locale][namespace]).sort()).toEqual(deepKeys(registeredResources.en[namespace]).sort())
      }
    })
  }
})
