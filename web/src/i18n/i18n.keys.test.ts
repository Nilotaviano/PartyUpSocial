import { describe, expect, it } from 'vitest'
import i18n, { resources } from './index'

type Resources = Record<string, unknown>

const locales = ['en', 'pt-BR', 'es'] as const
const namespaces = [
  'common',
  'auth',
  'onboarding',
  'profile',
  'settings',
  'errors',
  'friends',
  'sessions',
] as const

// Closed problem-code inventory (PHASE4_DESIGN.md §8): 23 endpoint codes plus
// the two blanket codes (rate_limited, transient_conflict) applied to every
// route. Every code below must render its own errors.codes.* message.
const problemCodes = [
  'session_not_found',
  'user_not_found',
  'session_validation',
  'invalid_if_match',
  'invalid_scope',
  'invalid_cursor',
  'session_conflict',
  'session_terminal',
  'session_time_locked',
  'capacity_below_occupancy',
  'waitlist_requires_capacity',
  'session_invites_closed',
  'session_responses_closed',
  'session_leave_closed',
  'session_guest_removal_closed',
  'join_request_exists',
  'join_request_not_pending',
  'join_request_closed',
  'no_session_capacity',
  'not_waitlisted',
  'guest_link_terminal',
  'guest_link_claimed',
  'reminder_not_eligible',
  'transient_conflict',
  'rate_limited',
] as const
const registeredResources = resources as Record<
  string,
  Record<string, Resources>
>

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
        expect(deepKeys(registeredResources[locale][namespace]).sort()).toEqual(
          deepKeys(registeredResources.en[namespace]).sort(),
        )
      }
    })
  }
})

describe('friends namespace rendering D-060', () => {
  for (const locale of locales) {
    it(locale + ' renders friends.actions.resolve', async () => {
      await i18n.changeLanguage(locale)
      expect(i18n.t('actions.resolve', { ns: 'friends' })).toBe(
        (resources[locale].friends as { actions: { resolve: string } }).actions
          .resolve,
      )
    })
  }
})

describe('sessions namespace parity and rendering D-060', () => {
  for (const locale of locales) {
    it(locale + ' renders sessions.create.heading', async () => {
      await i18n.changeLanguage(locale)
      expect(i18n.t('create.heading', { ns: 'sessions' })).toBe(
        (resources[locale].sessions as { create: { heading: string } })
          .create.heading,
      )
    })
  }

  it('has no guest.expired key (a session link is never "expired")', () => {
    for (const locale of locales) {
      const guest = (
        registeredResources[locale].sessions as {
          guest: Record<string, unknown>
        }
      ).guest
      expect(guest).not.toHaveProperty('expired')
    }
  })
})

describe('sessions problem-code coverage D-060/D-146', () => {
  it('errors.codes contains exactly the closed §8 problem-code inventory', () => {
    const codes = Object.keys(
      (
        registeredResources.en.sessions as {
          errors: { codes: Record<string, string> }
        }
      ).errors.codes,
    ).sort()
    expect(codes).toEqual([...problemCodes].sort())
  })

  for (const locale of locales) {
    it(locale + ' has one message per documented problem code', () => {
      const codes = (
        registeredResources[locale].sessions as {
          errors: { codes: Record<string, string> }
        }
      ).errors.codes
      for (const code of problemCodes) {
        expect(typeof codes[code]).toBe('string')
        expect(codes[code].length).toBeGreaterThan(0)
      }
    })
  }
})
