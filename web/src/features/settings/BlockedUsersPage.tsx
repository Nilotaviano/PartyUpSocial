import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ShieldCheck } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { EmptyState } from '../../components/shell/SectionCard'
import { IdentityRow } from '../../components/ui/identityRow'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { SubNav } from '../../components/ui/subNav'
import { AvatarDisc } from '../friends/AvatarDisc'
import {
  friendKeys,
  getBlockedUsers,
  invalidateFriendGraph,
  runFriendGraphAction,
} from '../friends/friendGraph'

export function SettingsNavigation() {
  const { t } = useTranslation('settings')

  return (
    <SubNav
      items={[
        { to: '/settings/profile', label: t('navigation.profile') },
        { to: '/settings/account', label: t('navigation.account') },
        { to: '/settings/blocked', label: t('navigation.blocked') },
        { to: '/settings/notifications', label: t('navigation.notifications') },
      ]}
      label={t('navigation.label')}
    />
  )
}

export function BlockedUsersPage() {
  const { t, i18n } = useTranslation(['friends', 'errors'])
  const queryClient = useQueryClient()
  const [status, setStatus] = useState('')
  const blocks = useQuery({
    queryKey: friendKeys.blocks,
    queryFn: getBlockedUsers,
  })
  const unblock = useMutation({
    mutationFn: runFriendGraphAction,
    onSuccess: async (_result, input) => {
      setStatus(t('status.unblocked'))
      await invalidateFriendGraph(queryClient, input)
    },
  })

  return (
    <main data-testid="screen-settings-blocked">
      <PageScaffold>
        <PageHeader
          description={t('empty.blockedHelp')}
          eyebrow={t('navigation.label', { ns: 'settings' })}
          title={t('pages.blocked')}
        />
        <SettingsNavigation />
        <p aria-live="polite" className="text-ink">
          {status}
        </p>

        {blocks.isPending && (
          <p aria-hidden="true" className="motion-safe:animate-pulse text-ink-soft">
            {t('status.loading')}
          </p>
        )}
        {blocks.error && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(blocks.error) ? blocks.error.code : 'generic', {
              ns: 'errors',
              defaultValue: t('generic', { ns: 'errors' }),
            })}
          </StatusBanner>
        )}
        {/* An empty blocked list is a good terminal state (§3.6): quiet, no CTA. */}
        {blocks.data?.length === 0 && (
          <EmptyState icon={ShieldCheck} title={t('empty.blocked')} />
        )}
        {blocks.data && blocks.data.length > 0 && (
          <Section label={t('pages.blocked')}>
            <ul>
              {blocks.data.map((blocked) => {
                const date = new Intl.DateTimeFormat(i18n.language, {
                  dateStyle: 'medium',
                }).format(new Date(blocked.createdAt))
                return (
                  <li
                    key={blocked.blockId}
                    className="border-t border-ink/25 first:border-t-0"
                  >
                    <IdentityRow
                      actions={
                        <Button
                          aria-busy={unblock.isPending}
                          aria-label={`${t('actions.unblock')} @${blocked.username}`}
                          disabled={unblock.isPending}
                          variant="neutral"
                          onClick={() =>
                            unblock.mutate({
                              kind: 'unblock',
                              username: blocked.username,
                              blockId: blocked.blockId,
                            })
                          }
                        >
                          {t('actions.unblock')}
                        </Button>
                      }
                      avatar={
                        /*
                          D-192: the disc is neutral — masked, seeded from the stored
                          username snapshot rather than a userId. `IBlockRepository`
                          deliberately holds no join to the blocked `User`, so a
                          live avatar here would make blocking a monitoring channel.
                        */
                        <AvatarDisc masked seed={blocked.username} size="sm" />
                      }
                      className="px-2 py-3"
                      displayName={
                        <span className="font-mono">@{blocked.username}</span>
                      }
                      meta={t('labels.blockedOn', { date })}
                    />
                  </li>
                )
              })}
            </ul>
          </Section>
        )}
        {unblock.error && (
          <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
            {t(isApiError(unblock.error) ? unblock.error.code : 'generic', {
              ns: 'errors',
              defaultValue: t('generic', { ns: 'errors' }),
            })}
          </p>
        )}
      </PageScaffold>
    </main>
  )
}
