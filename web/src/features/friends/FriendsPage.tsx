import { useQuery } from '@tanstack/react-query'
import { Mail, Send, UserPlus, Users } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { isApiError } from '../../api/client'
import { PageHeader } from '../../components/shell/PageHeader'
import { SectionCard } from '../../components/shell/SectionCard'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { StatusBanner } from '../../components/ui/statusBanner'
import { FriendCodeInput } from './FriendCodeInput'
import { FriendList } from './FriendList'
import { FriendRequests } from './FriendRequests'
import { ShareCard } from './ShareCard'
import { friendKeys, getFriendRequests, getFriends } from './friendGraph'

// Uplift header-action recipes (HomePage.tsx's exact classes).
const primaryButtonClasses =
  'inline-flex h-11 items-center justify-center gap-2 rounded-xl bg-primary px-4 font-display text-[13px] font-bold text-primary-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const outlineButtonClasses =
  'inline-flex h-11 items-center justify-center gap-2 rounded-xl border border-border bg-background px-4 font-display text-[13px] font-bold text-foreground hover:bg-muted/60 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

function Loading() {
  const { t } = useTranslation('friends')
  return (
    <p aria-hidden="true" className="motion-safe:animate-pulse text-ink-soft">
      {t('status.loading')}
    </p>
  )
}

/**
 * The header's primary action is a same-page jump, not a new route: a native
 * `href="#find-by-code-heading"` anchor so hash navigation (and the browser's
 * back/forward + no-JS fallback) still works, plus an imperative `.focus()`
 * on the code field itself — the anchor's default landing spot is the
 * section's non-focusable container, which would leave a keyboard/AT user
 * one more Tab away from typing a code.
 */
function AddFriendAction() {
  const { t } = useTranslation('friends')
  return (
    <a
      className={primaryButtonClasses}
      href="#find-by-code-heading"
      onClick={() => document.getElementById('friend-code')?.focus()}
    >
      <UserPlus aria-hidden className="size-4" />
      {t('actions.addFriend')}
    </a>
  )
}

export function FriendsPage() {
  const navigate = useNavigate()
  const { t } = useTranslation(['friends', 'errors'])
  const [status, setStatus] = useState('')
  const friends = useQuery({
    queryKey: friendKeys.friends,
    queryFn: getFriends,
  })
  const requests = useQuery({
    queryKey: friendKeys.requests,
    queryFn: getFriendRequests,
  })

  const error = friends.error ?? requests.error
  const friendItems = friends.data ?? []
  const incoming = requests.data?.incoming ?? []
  const outgoing = requests.data?.outgoing ?? []

  return (
    <main data-testid="screen-friends">
      <PageScaffold>
        <PageHeader
          actions={
            <>
              <AddFriendAction />
              <Link className={outlineButtonClasses} to="/friends/import/steam">
                {t('actions.importSteam')}
              </Link>
            </>
          }
          description={t('pages.listDescription')}
          eyebrow={t('pages.listEyebrow')}
          title={t('pages.list')}
        />

        <p aria-live="polite" className="text-ink">
          {status}
        </p>
        {error && (
          <StatusBanner role="alert" variant="danger">
            {t(isApiError(error) ? error.code : 'generic', {
              ns: 'errors',
              defaultValue: t('generic', { ns: 'errors' }),
            })}
          </StatusBanner>
        )}

        {/* `id` here (not on the SectionCard's own h2, which the shell
            component doesn't expose an id prop for) is the anchor target for
            both the header action above and no-JS hash navigation; `role`/
            `aria-label` give the region the landmark name the old `Section`
            wrapper carried via `aria-labelledby`. */}
        <div aria-label={t('sections.findByCode')} id="find-by-code-heading" role="region">
          <SectionCard icon={UserPlus} title={t('sections.findByCode')}>
            <FriendCodeInput
              onResolve={(friendCode) => navigate('/add/' + friendCode)}
            />
          </SectionCard>
        </div>

        <ShareCard />

        <div aria-label={t('sections.friends')} role="region">
          <SectionCard icon={Users} title={`${t('sections.friends')} · ${friendItems.length}`}>
            {friends.isPending ? (
              <Loading />
            ) : (
              <FriendList items={friendItems} onStatus={setStatus} />
            )}
          </SectionCard>
        </div>

        <div aria-label={t('sections.incoming')} role="region">
          <SectionCard icon={Mail} title={`${t('sections.incoming')} · ${incoming.length}`}>
            {requests.isPending ? (
              <Loading />
            ) : (
              <FriendRequests
                direction="incoming"
                items={incoming}
                onStatus={setStatus}
              />
            )}
          </SectionCard>
        </div>

        <div aria-label={t('sections.outgoing')} role="region">
          <SectionCard icon={Send} title={`${t('sections.outgoing')} · ${outgoing.length}`}>
            {requests.isPending ? (
              <Loading />
            ) : (
              <FriendRequests
                direction="outgoing"
                items={outgoing}
                onStatus={setStatus}
              />
            )}
          </SectionCard>
        </div>
      </PageScaffold>
    </main>
  )
}
