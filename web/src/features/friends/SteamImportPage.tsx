import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { AvatarDisc } from './AvatarDisc'
import {
  invalidateFriendGraph,
  runFriendGraphAction,
  type FriendGraphAction,
  type SteamImportResult,
} from './friendGraph'

type MatchState = 'actionable' | 'outgoing_pending' | 'accepted'
type MatchCard = {
  username: string
  state: MatchState
  avatar: { emoji: string | null }
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
  const heading = useRef<HTMLHeadingElement>(null)
  const [view, setView] = useState<ImportView>()
  const [status, setStatus] = useState('')

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const importer = useMutation({
    mutationFn: importSteamFriends,
    onSuccess: (result) => {
      setStatus('')
      setView({
        result,
        matches:
          'matches' in result
            ? result.matches.map((match) => ({
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
    <main
      className="mx-auto max-w-4xl space-y-6 p-4 sm:p-8"
      data-testid="screen-steam-import"
    >
      <h1
        ref={heading}
        tabIndex={-1}
        className="font-display text-display font-bold text-ink focus:outline-none"
      >
        {t('pages.importSteam')}
      </h1>
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
        <section aria-labelledby="steam-result-heading" className="space-y-4">
          <h2
            id="steam-result-heading"
            className="font-display text-h2 font-semibold text-ink"
          >
            {t(titleKey)}
          </h2>
          {bodyKey && <p className="text-ink-soft">{t(bodyKey)}</p>}
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
            <ul className="grid gap-3 sm:grid-cols-2">
              {view.matches.map((match, index) => (
                <li key={`${match.username}:${index}`}>
                  <article className="rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
                    <div className="flex items-center gap-2">
                      <AvatarDisc
                        emoji={match.avatar.emoji}
                        seed={match.username}
                        size="sm"
                      />
                      <h3 className="min-w-0 truncate font-mono font-semibold text-ink">
                        @{match.username}
                      </h3>
                    </div>
                    <p className="mt-1 text-ink-soft">
                      {t('steam.matchDescription', {
                        username: match.username,
                      })}
                    </p>
                    {match.state === 'actionable' ? (
                      <div className="mt-4 flex flex-wrap gap-2">
                        <Button
                          aria-busy={graphAction.isPending}
                          aria-label={`${t('actions.sendRequest')} @${match.username}`}
                          disabled={graphAction.isPending}
                          variant="neutral"
                          onClick={() =>
                            act({ kind: 'send', username: match.username })
                          }
                        >
                          {t('actions.sendRequest')}
                        </Button>
                        <Button
                          aria-busy={graphAction.isPending}
                          aria-label={`${t('actions.block')} @${match.username}`}
                          disabled={graphAction.isPending}
                          variant="danger"
                          onClick={() =>
                            act({ kind: 'block', username: match.username })
                          }
                        >
                          {t('actions.block')}
                        </Button>
                      </div>
                    ) : (
                      <p className="mt-3 text-ink-soft">
                        {t(
                          match.state === 'accepted'
                            ? 'status.friends'
                            : 'status.pendingOutgoing',
                        )}
                      </p>
                    )}
                    {match.state === 'actionable' && (
                      <p className="mt-3 text-ink-soft">
                        {t('profile.blockConsequence')}
                      </p>
                    )}
                  </article>
                </li>
              ))}
            </ul>
          )}

          {'unmatched' in result && result.unmatched.length > 0 && (
            <ul className="grid gap-3 sm:grid-cols-2">
              {result.unmatched.map((friend) => (
                <li key={friend.steamId64}>
                  <article className="flex h-full flex-col justify-between gap-4 rounded-card border-2 border-ink bg-card p-4 shadow-offset-card">
                    <div className="flex items-center gap-3">
                      {friend.avatarUrl ? (
                        <img
                          alt=""
                          className="h-12 w-12 rounded-full border-2 border-ink"
                          height="48"
                          src={friend.avatarUrl}
                          width="48"
                        />
                      ) : (
                        <AvatarDisc seed={friend.steamId64} size="sm" />
                      )}
                      <div className="min-w-0">
                        {friend.personaName && (
                          <h3 className="break-words font-display font-semibold text-ink">
                            {friend.personaName}
                          </h3>
                        )}
                        <p className="text-ink-soft">
                          {t('steam.unmatchedDescription')}
                        </p>
                      </div>
                    </div>
                    <Button
                      aria-label={`${t('actions.copyInvitationLink')} ${friend.personaName ?? friend.steamId64}`}
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
            <div className="rounded-card border-2 border-ink bg-card p-4">
              <p className="break-all text-ink-soft">
                {result.invitationUrl}
              </p>
              <Button
                className="mt-3"
                variant="neutral"
                onClick={() => void copyInvitation(result.invitationUrl)}
              >
                {t('actions.copyInvitationLink')}
              </Button>
            </div>
          )}
        </section>
      )}
    </main>
  )
}
