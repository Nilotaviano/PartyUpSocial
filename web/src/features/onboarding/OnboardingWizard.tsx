import { useEffect, useReducer, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useNavigate } from 'react-router'
import type { components } from '../../api/schema'
import {
  ApiError,
  client,
  isApiError,
  refreshAntiforgeryToken,
} from '../../api/client'
import {
  readFriendContinuation,
  withFriendContinuation,
} from '../../auth/friendContinuation'
import { getSession, sessionQueryKey, useSession } from '../../auth/useSession'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import { FriendCodeInput } from '../friends/FriendCodeInput'
import { FriendCodeResolution } from '../friends/AddFriendPage'
import { ProfileResolution } from '../friends/ProfilePage'
import { ShareCard } from '../friends/ShareCard'

type ProfileDraft = components['schemas']['ProfileDraft']
type AvatarDraft = components['schemas']['AvatarDraft']
type ActivationState = 'idle' | 'refreshing' | 'ready' | 'error'

const storageKey = 'partyup.onboarding-draft'
const steps = [
  'username',
  'display-name',
  'avatar',
  'time-zone',
  'link-platforms',
  'friends',
] as const
type Step = (typeof steps)[number]

function emptyDraft(): ProfileDraft {
  return {
    username: null,
    displayName: null,
    avatar: null,
    timeZone: null,
    locale: null,
  }
}

function loadDraft(): ProfileDraft {
  try {
    const saved: unknown = JSON.parse(
      sessionStorage.getItem(storageKey) ?? 'null',
    )
    return saved && typeof saved === 'object'
      ? { ...emptyDraft(), ...(saved as Partial<ProfileDraft>) }
      : emptyDraft()
  } catch {
    return emptyDraft()
  }
}

function reducer(state: ProfileDraft, update: Partial<ProfileDraft>) {
  return { ...state, ...update }
}

function fromPath(path: string): Step | undefined {
  return steps.find((step) => path.endsWith('/' + step))
}

function nextStep(draft: ProfileDraft): Step {
  if (!draft.username) return 'username'
  if (!draft.displayName) return 'display-name'
  if (!draft.avatar) return 'avatar'
  if (!draft.timeZone) return 'time-zone'
  return 'link-platforms'
}

function errorText(
  error: unknown,
  t: (key: string, options?: object) => string,
) {
  return t(isApiError(error) ? error.code : 'generic', {
    defaultValue: t('generic'),
  })
}

function availabilityError(reason: string | null | undefined) {
  switch (reason) {
    case 'taken':
      return 'username.taken'
    case 'reserved':
      return 'username.reserved'
    case 'cooldown':
      return 'username.cooldown'
    case 'profanity':
      return 'text.profanity'
    case 'invalid':
      return 'text.invalid'
    default:
      return 'generic'
  }
}

export function UsernameStep({
  value,
  onNext,
}: {
  value: string | null
  onNext: (username: string) => void
}) {
  const { t } = useTranslation(['onboarding', 'errors'])
  const [username, setUsername] = useState(value ?? '')
  const [error, setError] = useState<unknown>()

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(undefined)
    try {
      const { data } = await client.GET(
        '/api/v1/onboarding/username/availability',
        {
          params: { query: { username } },
        },
      )
      if (!data?.available) {
        setError(new ApiError(400, availabilityError(data?.reason)))
        return
      }

      onNext(username)
    } catch (caught) {
      setError(caught)
    }
  }

  const message = error
    ? errorText(error, (key, options) => t(key, { ns: 'errors', ...options }))
    : null

  return (
    <form onSubmit={submit}>
      <Label htmlFor="username">{t('fields.username')}</Label>
      <Input
        id="username"
        value={username}
        onChange={(event) => setUsername(event.target.value)}
        aria-describedby={message ? 'username-error' : undefined}
        required
      />
      {message && (
        <p id="username-error" role="alert">
          {message}
        </p>
      )}
      <Button type="submit">{t('actions.continue')}</Button>
    </form>
  )
}

function DisplayNameStep({
  value,
  onNext,
}: {
  value: string | null
  onNext: (displayName: string) => void
}) {
  const { t } = useTranslation('onboarding')
  const [displayName, setDisplayName] = useState(value ?? '')
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        onNext(displayName)
      }}
    >
      <Label htmlFor="display-name">{t('fields.displayName')}</Label>
      <Input
        id="display-name"
        value={displayName}
        onChange={(event) => setDisplayName(event.target.value)}
        required
      />
      <Button type="submit">{t('actions.continue')}</Button>
    </form>
  )
}

function AvatarStep({
  value,
  onNext,
}: {
  value: AvatarDraft | null
  onNext: (avatar: AvatarDraft) => void
}) {
  const { t } = useTranslation('onboarding')
  const [emoji, setEmoji] = useState(value?.emoji ?? 'game')
  return (
    <div role="radiogroup" aria-label={t('fields.avatar')}>
      {['game', 'dice', 'arcade'].map((option) => (
        <button
          key={option}
          type="button"
          role="radio"
          aria-checked={emoji === option}
          onClick={() => setEmoji(option)}
        >
          {option}
        </button>
      ))}
      <Button
        onClick={() => onNext({ kind: 'emoji', emoji, platformLinkId: null })}
      >
        {t('actions.continue')}
      </Button>
    </div>
  )
}

function TimeZoneStep({
  value,
  onNext,
}: {
  value: string | null
  onNext: (timeZone: string) => void
}) {
  const { t } = useTranslation('onboarding')
  const [timeZone, setTimeZone] = useState(
    value ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
  )
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        onNext(timeZone)
      }}
    >
      <Label htmlFor="time-zone">{t('fields.timeZone')}</Label>
      <select
        id="time-zone"
        value={timeZone}
        onChange={(event) => setTimeZone(event.target.value)}
      >
        <option value={timeZone}>{timeZone}</option>
        <option value="UTC">UTC</option>
      </select>
      <Button type="submit">{t('actions.continue')}</Button>
    </form>
  )
}

function ContinuationCard({ continuation }: { continuation: string }) {
  if (continuation.startsWith('/u/')) {
    return <ProfileResolution username={continuation.slice(3)} />
  }

  return <FriendCodeResolution friendCode={continuation.slice(5)} />
}

function FriendsEducation({
  continuation,
}: {
  continuation: string | undefined
}) {
  const { t } = useTranslation('friends')
  const navigate = useNavigate()

  return (
    <div className="space-y-6">
      <p className="max-w-prose text-slate-700">{t('onboarding.body')}</p>
      {continuation && (
        <section
          aria-labelledby="continued-profile-heading"
          className="space-y-3"
        >
          <h2
            id="continued-profile-heading"
            className="text-xl font-semibold text-slate-950"
          >
            {t('onboarding.continuation')}
          </h2>
          <ContinuationCard continuation={continuation} />
        </section>
      )}
      <section
        aria-labelledby="find-by-code-heading"
        className="space-y-3 rounded-xl border border-slate-200 bg-white p-5"
      >
        <h2
          id="find-by-code-heading"
          className="text-xl font-semibold text-slate-950"
        >
          {t('sections.findByCode')}
        </h2>
        <FriendCodeInput
          onResolve={(friendCode) => navigate('/add/' + friendCode)}
        />
      </section>
      <ShareCard />
      <section aria-labelledby="steam-onboarding-heading" className="space-y-2">
        <h2
          id="steam-onboarding-heading"
          className="text-xl font-semibold text-slate-950"
        >
          {t('pages.importSteam')}
        </h2>
        <p>{t('onboarding.steamOptional')}</p>
        <Link className="text-indigo-700 underline" to="/friends/import/steam">
          {t('actions.importSteam')}
        </Link>
      </section>
      <div className="flex flex-wrap gap-3">
        <Button onClick={() => navigate('/app', { replace: true })}>
          {t('actions.finishSetup')}
        </Button>
        <Button onClick={() => navigate('/app', { replace: true })}>
          {t('actions.skipForNow')}
        </Button>
      </div>
    </div>
  )
}

export function OnboardingWizard() {
  const { t, i18n } = useTranslation(['onboarding', 'friends', 'errors'])
  const navigate = useNavigate()
  const location = useLocation()
  const queryClient = useQueryClient()
  const { data: session } = useSession()
  const isActive =
    session?.authenticated === true &&
    session.state !== 'onboarding' &&
    session.onboarding === null
  const [draft, dispatch] = useReducer(reducer, undefined, loadDraft)
  const [activation, setActivation] = useState<ActivationState>(() =>
    isActive ? 'ready' : 'idle',
  )
  const [refreshError, setRefreshError] = useState<unknown>()
  const complete = useMutation({
    mutationFn: (body: ProfileDraft) =>
      client.POST('/api/v1/onboarding/complete', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body,
      }),
  })
  const heading = useRef<HTMLHeadingElement>(null)
  const continuation = readFriendContinuation(location.search)
  const requested = fromPath(location.pathname)
  const activationAllowsFriends = isActive || activation !== 'idle'
  const allowed: Step = activationAllowsFriends ? 'friends' : nextStep(draft)
  const requestedIndex = requested === undefined ? -1 : steps.indexOf(requested)
  const current: Step = isActive
    ? 'friends'
    : requested === undefined || requestedIndex > steps.indexOf(allowed)
      ? allowed
      : requested

  useEffect(() => {
    sessionStorage.setItem(storageKey, JSON.stringify(draft))
  }, [draft])

  useEffect(() => {
    if (requested !== current) {
      navigate(withFriendContinuation('/onboarding/' + current, continuation), {
        replace: true,
      })
    }
  }, [continuation, current, navigate, requested])

  useEffect(() => {
    heading.current?.focus()
  }, [current])

  function advance(update: Partial<ProfileDraft>) {
    const updated = { ...draft, ...update }
    dispatch(update)
    navigate(
      withFriendContinuation('/onboarding/' + nextStep(updated), continuation),
    )
  }

  async function refreshActivatedSession() {
    setActivation('refreshing')
    setRefreshError(undefined)
    try {
      await refreshAntiforgeryToken()
      await queryClient.fetchQuery({
        queryKey: sessionQueryKey,
        queryFn: getSession,
        staleTime: 0,
      })
      setActivation('ready')
    } catch (caught) {
      setRefreshError(caught)
      setActivation('error')
    }
  }

  async function activate() {
    if (complete.isPending) return
    try {
      await complete.mutateAsync({ ...draft, locale: i18n.language })
      sessionStorage.removeItem(storageKey)
      setActivation('refreshing')
      navigate(withFriendContinuation('/onboarding/friends', continuation), {
        replace: true,
      })
      await refreshActivatedSession()
    } catch {
      return
    }
  }

  const labels: Record<Step, string> = {
    username: 'pages.username',
    'display-name': 'pages.displayName',
    avatar: 'pages.avatar',
    'time-zone': 'pages.timeZone',
    'link-platforms': 'pages.linkPlatforms',
    friends: 'pages.friends',
  }

  return (
    <main
      className="mx-auto max-w-3xl space-y-5 p-4 sm:p-8"
      data-testid="screen-onboarding"
    >
      <p aria-live="polite">
        {t('progress', {
          current: steps.indexOf(current) + 1,
          total: steps.length,
          step: t(labels[current]),
        })}
      </p>
      <h1
        ref={heading}
        tabIndex={-1}
        className="text-2xl font-bold tracking-tight text-slate-950"
      >
        {t(labels[current])}
      </h1>
      {current === 'username' && (
        <UsernameStep
          value={draft.username}
          onNext={(username) => advance({ username })}
        />
      )}
      {current === 'display-name' && (
        <DisplayNameStep
          value={draft.displayName}
          onNext={(displayName) => advance({ displayName })}
        />
      )}
      {current === 'avatar' && (
        <AvatarStep
          value={draft.avatar}
          onNext={(avatar) => advance({ avatar })}
        />
      )}
      {current === 'time-zone' && (
        <TimeZoneStep
          value={draft.timeZone}
          onNext={(timeZone) => advance({ timeZone })}
        />
      )}
      {current === 'link-platforms' && (
        <>
          <p>{t('messages.platformLinksOptional')}</p>
          <Button
            aria-busy={complete.isPending}
            disabled={complete.isPending}
            onClick={() => void activate()}
          >
            {t('actions.continue')}
          </Button>
          {complete.error && (
            <p role="alert">
              {errorText(complete.error, (key, options) =>
                t(key, { ns: 'errors', ...options }),
              )}
            </p>
          )}
        </>
      )}
      {current === 'friends' && activation === 'refreshing' && (
        <p role="status">{t('friends:status.loading')}</p>
      )}
      {current === 'friends' && activation === 'error' && (
        <section aria-labelledby="activation-refresh-error">
          <h2 id="activation-refresh-error">
            {t('friends:onboarding.refreshFailed')}
          </h2>
          <p role="alert">
            {errorText(refreshError, (key, options) =>
              t(key, { ns: 'errors', ...options }),
            )}
          </p>
          <Button onClick={() => void refreshActivatedSession()}>
            {t('friends:actions.retry')}
          </Button>
        </section>
      )}
      {current === 'friends' && activation === 'ready' && (
        <FriendsEducation continuation={continuation} />
      )}
    </main>
  )
}
