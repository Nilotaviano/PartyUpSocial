import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { IdentityRow } from '../../components/ui/identityRow'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { AvatarDisc } from './AvatarDisc'
import { avatarImgUrl } from './avatarImage'
import {
  invalidateFriendGraph,
  runFriendGraphAction,
  type FriendGraphAction,
  type SteamImportResult,
} from './friendGraph'

type MatchState = 'actionable' | 'outgoing_pending' | 'accepted'
type MatchCard = {
  userId: string
  username: string
  state: MatchState
  avatar: { emoji: string | null; imgUrl?: string | null }
}
type ImportView = {
  result: SteamImportResult
  matches: MatchCard[]
}

async function importSteamFriends() {
  const { data } = await client.POST('/api/v1/friends/imports/steam', {
    params: { header: { 'X-CSRF-TOKEN': '' } },
  })
  if (!data) throw new Error('Steam import response was empty.')
  return data
}

export function SteamImportPage() {
  const { t } = useTranslation(['friends', 'errors'])
  const queryClient = useQueryClient()
  const [view, setView] = useState<ImportView>()
  const [status, setStatus] = useState('')

  const importer = useMutation({
    mutationFn: importSteamFriends,
    onSuccess: (result) => {
      setStatus('')
      setView({
        result,
        matches:
          'matches' in result
            ? result.matches.map((match) => ({
                userId: match.userId,
                username: match.username,
                state: 'actionable' as const,
                avatar: match.avatar,
              }))
            : [],
      })
    },
  })

  const graphAction = useMutation({
    mutationFn: runFriendGraphAction,
    onSuccess: async (resource, input) => {
      if (input.kind === 'send') {
        const nextState: MatchState =
          resource?.status === 'accepted' ? 'accepted' : 'outgoing_pending'
        setView((current) =>
          current
            ? {
                ...current,
                matches: current.matches.map((match) =>
                  match.username === input.username
                    ? { ...match, state: nextState }
                    : match,
                ),
              }
            : current,
        )
        setStatus(t('status.requestSent'))
      } else if (input.kind === 'block') {
        setView((current) =>
          current
            ? {
                ...current,
                matches: current.matches.filter(
                  (match) => match.username !== input.username,
                ),
              }
            : current,
        )
        setStatus(t('status.blocked'))
      }
      await invalidateFriendGraph(queryClient, input)
    },
  })

  async function copyInvitation(url: string) {
    await navigator.clipboard.writeText(url)
    setStatus(t('live.copied', { label: t('actions.copyInvitationLink') }))
  }

  const result = view?.result
  const titleKey = result
    ? {
        ready: 'steam.readyTitle',
        private_profile: 'steam.privateTitle',
        zero_friends: 'steam.zeroFriendsTitle',
        zero_matches: 'steam.zeroMatchesTitle',
        not_configured: 'steam.notConfiguredTitle',
        upstream_failure: 'steam.upstreamFailureTitle',
      }[result.state]
    : null
  const bodyKey = result
    ? {
        ready: null,
        private_profile: null,
        zero_friends: 'steam.zeroFriendsBody',
        zero_matches: 'steam.zeroMatchesBody',
        not_configured: 'steam.notConfiguredBody',
        upstream_failure: 'steam.upstreamFailureBody',
      }[result.state]
    : null
  const error = importer.error ?? graphAction.error

  function act(input: FriendGraphAction) {
    graphAction.mutate(input)
  }

  return (
    <main data-testid="screen-steam-import">
      <PageScaffold>
        <PageHeader
          actions={
            <Button
              aria-busy={importer.isPending}
              disabled={importer.isPending}
              variant="primary"
              onClick={() => importer.mutate()}
            >
              {result?.state === 'upstream_failure'
                ? t('actions.retry')
                : t('actions.importSteam')}
            </Button>
          }
          back={{ to: '/friends', label: t('pages.backToFriends') }}
          title={t('pages.importSteam')}
        />

        {error && (
          <p ref={(node) => node?.focus()} role="alert" tabIndex={-1}>
            {t(isApiError(error) ? error.code : 'generic', {
              ns: 'errors',
              defaultValue: t('generic', { ns: 'errors' }),
            })}
          </p>
        )}
        <p aria-live="polite" className="text-ink">
          {status}
        </p>

        {result && titleKey && (
          <Section
            description={bodyKey ? t(bodyKey) : undefined}
            title={t(titleKey)}
            titleId="steam-result-heading"
          >
            {result.state === 'private_profile' && (
              <a
                className="inline-flex min-h-11 items-center text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                href="https://steamcommunity.com/my/edit/settings"
                rel="noreferrer"
                target="_blank"
              >
                {t('steam.privateHelp')}
              </a>
            )}

            {view.matches.length > 0 && (
              <ul className="flex flex-col border-b border-ink/25">
                {view.matches.map((match, index) => (
                  <li key={`${match.username}:${index}`}>
                    <article className="flex flex-col gap-2 border-t border-ink/25 py-3">
                      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
                        <div className="flex min-w-0 flex-1 items-center gap-3">
                          <AvatarDisc
                            emoji={match.avatar.emoji}
                            imgUrl={avatarImgUrl(match.avatar)}
                            seed={match.userId}
                            size="sm"
                          />
                          {/* D-068/D-188: a Steam match exposes the username only. */}
                          <IdentityRow
                            meta={t('steam.matchDescription', {
                              username: match.username,
                            })}
                            nameAs="h3"
                            username={match.username}
                            variant="handle"
                          />
                        </div>
                        {match.state === 'actionable' ? (
                          <div className="flex flex-none flex-wrap items-center gap-2">
                            <Button
                              aria-busy={graphAction.isPending}
                              aria-label={`${t('actions.sendRequest')} @${match.username}`}
                              disabled={graphAction.isPending}
                              variant="primary"
                              onClick={() =>
                                act({ kind: 'send', username: match.username })
                              }
                            >
                              {t('actions.sendRequest')}
                            </Button>
                            {/*
                              §3.3: block stays one plain danger-text button with
                              its consequence sentence visible under the row.
                            */}
                            <Button
                              aria-busy={graphAction.isPending}
                              aria-label={`${t('actions.block')} @${match.username}`}
                              className="text-danger"
                              disabled={graphAction.isPending}
                              variant="ghost"
                              onClick={() =>
                                act({ kind: 'block', username: match.username })
                              }
                            >
                              {t('actions.block')}
                            </Button>
                          </div>
                        ) : (
                          <p className="flex-none text-meta text-ink-soft">
                            {t(
                              match.state === 'accepted'
                                ? 'status.friends'
                                : 'status.pendingOutgoing',
                            )}
                          </p>
                        )}
                      </div>
                      {match.state === 'actionable' && (
                        <p className="text-meta text-ink-soft">
                          {t('profile.blockConsequence')}
                        </p>
                      )}
                    </article>
                  </li>
                ))}
              </ul>
            )}

            {'unmatched' in result && result.unmatched.length > 0 && (
              <ul className="flex flex-col border-b border-ink/25">
                {result.unmatched.map((friend) => (
                  <li key={friend.steamId64}>
                    <article className="flex flex-col gap-3 border-t border-ink/25 py-3 sm:flex-row sm:items-center">
                      <IdentityRow
                        avatar={
                          friend.avatarUrl ? (
                            <img
                              alt=""
                              className="h-12 w-12 flex-none rounded-full border-2 border-ink"
                              height="48"
                              src={friend.avatarUrl}
                              width="48"
                            />
                          ) : (
                            <AvatarDisc seed={friend.steamId64} size="sm" />
                          )
                        }
                        className="flex-1"
                        // An unmatched Steam friend may carry no persona name at
                        // all; the description becomes the row's name rather
                        // than leaving an empty heading behind.
                        displayName={
                          friend.personaName ?? t('steam.unmatchedDescription')
                        }
                        meta={
                          friend.personaName
                            ? t('steam.unmatchedDescription')
                            : undefined
                        }
                        nameAs={friend.personaName ? 'h3' : 'span'}
                      />
                      <Button
                        aria-label={`${t('actions.copyInvitationLink')} ${friend.personaName ?? friend.steamId64}`}
                        className="flex-none"
                        variant="neutral"
                        onClick={() => void copyInvitation(friend.invitationUrl)}
                      >
                        {t('actions.copyInvitationLink')}
                      </Button>
                    </article>
                  </li>
                ))}
              </ul>
            )}

            {(result.state === 'not_configured' ||
              result.state === 'zero_matches' ||
              result.state === 'zero_friends' ||
              result.state === 'private_profile' ||
              result.state === 'upstream_failure') && (
              <div className="grid grid-cols-[1fr_auto] items-center gap-2 rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
                <a
                  className="flex min-h-11 min-w-0 items-center truncate rounded-control text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                  href={result.invitationUrl}
                >
                  <span className="truncate">{result.invitationUrl}</span>
                </a>
                <Button
                  variant="neutral"
                  onClick={() => void copyInvitation(result.invitationUrl)}
                >
                  {t('actions.copyInvitationLink')}
                </Button>
              </div>
            )}
          </Section>
        )}
      </PageScaffold>
    </main>
  )
}
