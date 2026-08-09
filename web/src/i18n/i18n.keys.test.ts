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
  'availability',
  'home',
  'polls',
] as const

// Closed problem-code inventory (PHASE6_DESIGN.md §4.2/§7): the poll HTTP
// surface's own codes, held in polls.json the same way sessions.json holds
// its own set. `invalid_scope`/`invalid_cursor`/`rate_limited` are shared
// wording with sessions.json, duplicated here rather than cross-namespace
// referenced (each namespace stays self-contained per the shipped pattern).
const pollProblemCodes = [
  'poll_not_found',
  'poll_validation',
  'poll_option_cap',
  'poll_option_not_found',
  'poll_voting_closed',
  'poll_terminal',
  'poll_invalid_transition',
  'poll_participant_removed',
  'poll_conflict',
  'poll_guest_not_found',
  'poll_selection_window_closed',
  'poll_zero_options',
  'user_not_found',
  'invalid_scope',
  'invalid_cursor',
  'rate_limited',
] as const

// Closed problem-code inventory (PHASE5_DESIGN.md §8/§10.2): the five new
// availability codes. The four pre-existing codes it reuses (invalid_scope,
// invalid_cursor, rate_limited, transient_conflict) stay in sessions.json.
const availabilityProblemCodes = [
  'availability_not_found',
  'availability_validation',
  'invalid_range',
  'range_too_large',
  'occurrence_not_found',
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

describe('polls namespace parity and rendering D-060 (PHASE6_DESIGN.md §4.2)', () => {
  for (const locale of locales) {
    it(locale + ' renders polls.create.heading', async () => {
      await i18n.changeLanguage(locale)
      expect(i18n.t('create.heading', { ns: 'polls' })).toBe(
        (resources[locale].polls as { create: { heading: string } }).create
          .heading,
      )
    })
  }

  it('errors.codes contains exactly the closed poll problem-code inventory', () => {
    const codes = Object.keys(
      (
        registeredResources.en.polls as {
          errors: { codes: Record<string, string> }
        }
      ).errors.codes,
    ).sort()
    expect(codes).toEqual([...pollProblemCodes].sort())
  })

  for (const locale of locales) {
    it(locale + ' has one message per documented poll problem code', () => {
      const codes = (
        registeredResources[locale].polls as {
          errors: { codes: Record<string, string> }
        }
      ).errors.codes
      for (const code of pollProblemCodes) {
        expect(typeof codes[code]).toBe('string')
        expect(codes[code].length).toBeGreaterThan(0)
      }
    })
  }
})

describe('availability problem-code coverage D-060 (PHASE5_DESIGN.md §8/§10.2)', () => {
  it('errors.codes contains exactly the five new availability problem codes', () => {
    const codes = Object.keys(
      (
        registeredResources.en.availability as {
          errors: { codes: Record<string, string> }
        }
      ).errors.codes,
    ).sort()
    expect(codes).toEqual([...availabilityProblemCodes].sort())
  })

  for (const locale of locales) {
    it(locale + ' has one message per new availability problem code', () => {
      const codes = (
        registeredResources[locale].availability as {
          errors: { codes: Record<string, string> }
        }
      ).errors.codes
      for (const code of availabilityProblemCodes) {
        expect(typeof codes[code]).toBe('string')
        expect(codes[code].length).toBeGreaterThan(0)
      }
    })
  }
})
