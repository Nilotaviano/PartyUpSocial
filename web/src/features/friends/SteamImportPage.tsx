import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import {
  invalidateFriendGraph,
  runFriendGraphAction,
  type FriendGraphAction,
  type SteamImportResult,
} from './friendGraph'

type MatchState = 'actionable' | 'outgoing_pending' | 'accepted'
type MatchCard = { username: string; state: MatchState }
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
        className="text-3xl font-bold tracking-tight text-slate-950 focus:outline-none"
      >
        {t('pages.importSteam')}
      </h1>
      <Button
        aria-busy={importer.isPending}
        className="min-h-11 rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
        disabled={importer.isPending}
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
      <p aria-live="polite" className="text-sm text-emerald-700">
        {status}
      </p>

      {result && titleKey && (
        <section aria-labelledby="steam-result-heading" className="space-y-4">
          <h2
            id="steam-result-heading"
            className="text-xl font-semibold text-slate-950"
          >
            {t(titleKey)}
          </h2>
          {bodyKey && <p className="text-slate-700">{t(bodyKey)}</p>}
          {result.state === 'private_profile' && (
            <a
              className="inline-flex min-h-11 items-center text-indigo-700 underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
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
                  <article className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                    <h3 className="font-mono font-semibold text-slate-950">
                      @{match.username}
                    </h3>
                    <p className="mt-1 text-sm text-slate-600">
                      {t('steam.matchDescription', {
                        username: match.username,
                      })}
                    </p>
                    {match.state === 'actionable' ? (
                      <div className="mt-4 flex flex-wrap gap-2">
                        <Button
                          aria-busy={graphAction.isPending}
                          aria-label={`${t('actions.sendRequest')} @${match.username}`}
                          className="min-h-11 rounded-md bg-indigo-600 px-4 py-2 text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-60"
                          disabled={graphAction.isPending}
                          onClick={() =>
                            act({ kind: 'send', username: match.username })
                          }
                        >
                          {t('actions.sendRequest')}
                        </Button>
                        <Button
                          aria-busy={graphAction.isPending}
                          aria-label={`${t('actions.block')} @${match.username}`}
                          className="min-h-11 rounded-md border border-red-300 px-4 py-2 text-red-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-700 disabled:opacity-60"
                          disabled={graphAction.isPending}
                          onClick={() =>
                            act({ kind: 'block', username: match.username })
                          }
                        >
                          {t('actions.block')}
                        </Button>
                      </div>
                    ) : (
                      <p className="mt-3 text-sm text-slate-700">
                        {t(
                          match.state === 'accepted'
                            ? 'status.friends'
                            : 'status.pendingOutgoing',
                        )}
                      </p>
                    )}
                    {match.state === 'actionable' && (
                      <p className="mt-3 text-sm text-slate-600">
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
                  <article className="flex h-full flex-col justify-between gap-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                    <div className="flex items-center gap-3">
                      {friend.avatarUrl ? (
                        <img
                          alt=""
                          className="h-12 w-12 rounded-full"
                          height="48"
                          src={friend.avatarUrl}
                          width="48"
                        />
                      ) : (
                        <span
                          aria-hidden="true"
                          className="grid h-12 w-12 place-items-center rounded-full bg-slate-100"
                        >
                          ●
                        </span>
                      )}
                      <div className="min-w-0">
                        {friend.personaName && (
                          <h3 className="break-words font-semibold text-slate-950">
                            {friend.personaName}
                          </h3>
                        )}
                        <p className="text-sm text-slate-600">
                          {t('steam.unmatchedDescription')}
                        </p>
                      </div>
                    </div>
                    <Button
                      aria-label={`${t('actions.copyInvitationLink')} ${friend.personaName ?? friend.steamId64}`}
                      className="min-h-11 rounded-md border border-slate-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
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
            <div className="rounded-lg bg-indigo-50 p-4">
              <p className="break-all text-sm text-slate-700">
                {result.invitationUrl}
              </p>
              <Button
                className="mt-3 min-h-11 rounded-md border border-indigo-300 px-4 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
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
