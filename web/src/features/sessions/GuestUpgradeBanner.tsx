import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { isApiError } from '../../api/client'
import {
  validateFriendContinuation,
  withFriendContinuation,
} from '../../auth/friendContinuation'
import { useSession } from '../../auth/useSession'
import { Button } from '../../components/ui/button'
import { Section } from '../../components/ui/section'
import { guestPollKeys, upgradeGuestPoll } from '../polls/guestPollQueries'
import { guestSessionKeys, upgradeGuestSession } from './guestSessionQueries'

type GuestUpgradeBannerProps = {
  guestLinkId: string
  kind: 'session' | 'poll'
}

const quietLinkClasses =
  'inline-flex min-h-11 w-fit items-center font-body font-semibold text-ink underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

/**
 * D-225: the claimed guest's route out of guesthood. It renders only on a live, claimed link —
 * the seat has to exist before it can be moved — and only ever offers the step the visitor is
 * actually able to take next: sign in, finish onboarding, or link the seat. The guest cookie in
 * this browser is what proves "I am that guest", so the upgrade needs no identifier beyond the
 * link id already in the URL.
 */
/**
 * Codes the guest page already has a screen for: the link is no longer live, or was never
 * claimed. Re-reading the link is the whole handling — the shipped terminal notice takes over.
 */
const reresolvingCodes = new Set([
  'guest_link_terminal',
  'guest_link_unclaimed',
  'session_not_found',
  'poll_not_found',
])

export function GuestUpgradeBanner({ guestLinkId, kind }: GuestUpgradeBannerProps) {
  const { t } = useTranslation('sessions')
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: session, isPending } = useSession()

  const guestKey =
    kind === 'poll' ? guestPollKeys.detail(guestLinkId) : guestSessionKeys.detail(guestLinkId)

  const upgrade = useMutation<{ pollId: string } | { sessionId: string }>({
    mutationFn: () =>
      kind === 'poll' ? upgradeGuestPoll(guestLinkId) : upgradeGuestSession(guestLinkId),
    onSuccess: async (result) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: guestKey }),
        queryClient.invalidateQueries({ queryKey: ['sessions'] }),
        queryClient.invalidateQueries({ queryKey: ['polls'] }),
      ])
      navigate(
        'pollId' in result ? `/polls/${result.pollId}` : `/sessions/${result.sessionId}`,
      )
    },
    onError: (error) => {
      if (isApiError(error) && reresolvingCodes.has(error.code)) {
        return queryClient.invalidateQueries({ queryKey: guestKey })
      }
    },
  })

  if (isPending) {
    return null
  }

  const continuation = validateFriendContinuation(`/guest/${guestLinkId}`)

  if (!session?.authenticated) {
    return (
      <Section title={t('guest.upgrade.heading')} titleId="guest-upgrade-heading">
        <p className="text-ink-soft">{t('guest.upgrade.signedOut')}</p>
        <Link className={quietLinkClasses} to={withFriendContinuation('/login', continuation)}>
          {t('guest.upgrade.signIn')}
        </Link>
      </Section>
    )
  }

  if (session.state === 'onboarding' || session.onboarding !== null) {
    return (
      <Section title={t('guest.upgrade.heading')} titleId="guest-upgrade-heading">
        <p className="text-ink-soft">{t('guest.upgrade.onboarding')}</p>
        <Link
          className={quietLinkClasses}
          to={withFriendContinuation('/onboarding', continuation)}
        >
          {t('guest.upgrade.finishSetup')}
        </Link>
      </Section>
    )
  }

  // Whatever the refusal was, the visitor is told the same thing: a blocked pair and a plain
  // conflict must be indistinguishable here (D-225).
  const failed =
    upgrade.error != null &&
    !(isApiError(upgrade.error) && reresolvingCodes.has(upgrade.error.code))

  return (
    <Section title={t('guest.upgrade.heading')} titleId="guest-upgrade-heading">
      <p className="text-ink-soft">{t('guest.upgrade.signedIn')}</p>
      <Button
        aria-busy={upgrade.isPending}
        className="self-start"
        disabled={upgrade.isPending}
        type="button"
        variant="primary"
        onClick={() => upgrade.mutate()}
      >
        {t('guest.upgrade.linkSeat', { name: session.profile?.displayName ?? '' })}
      </Button>
      {failed && (
        <p role="alert" className="text-ink">
          {t('guest.upgrade.failed')}
        </p>
      )}
    </Section>
  )
}
