import { useEffect, useReducer, useState, type FormEvent } from 'react'
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
import { BrandMark } from '../../components/ui/brandMark'
import { Button } from '../../components/ui/button'
import { FormField, formShellClasses } from '../../components/ui/formShell'
import { Input } from '../../components/ui/input'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section, SectionCard } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'
import { TimeZoneField, isKnownTimeZone } from '../../components/TimeZoneField'
import { FriendCodeInput } from '../friends/FriendCodeInput'
import { FriendCodeResolution } from '../friends/AddFriendPage'
import { ProfileResolution } from '../friends/ProfilePage'
import { ShareCard } from '../friends/ShareCard'
import { AVATAR_CATALOG } from '../profile/avatarCatalog'
import { AvatarPicker } from '../profile/AvatarPicker'

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
    <form className="flex flex-col gap-5" onSubmit={submit}>
      <FormField htmlFor="username" label={t('fields.username')}>
        <Input
          id="username"
          value={username}
          onChange={(event) => setUsername(event.target.value)}
          aria-describedby={message ? 'username-error' : undefined}
          required
        />
      </FormField>
      {message && (
        <p id="username-error" role="alert">
          {message}
        </p>
      )}
      <div className="flex">
        <Button className="ml-auto" type="submit" variant="primary">
          {t('actions.continue')}
        </Button>
      </div>
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
      className="flex flex-col gap-5"
      onSubmit={(event) => {
        event.preventDefault()
        onNext(displayName)
      }}
    >
      <FormField htmlFor="display-name" label={t('fields.displayName')}>
        <Input
          id="display-name"
          value={displayName}
          onChange={(event) => setDisplayName(event.target.value)}
          required
        />
      </FormField>
      <div className="flex">
        <Button className="ml-auto" type="submit" variant="primary">
          {t('actions.continue')}
        </Button>
      </div>
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
  const [emoji, setEmoji] = useState(value?.emoji ?? AVATAR_CATALOG[0].glyph)
  return (
    <div className="flex flex-col gap-5">
      <AvatarPicker idPrefix="onboarding-avatar" value={emoji} onChange={setEmoji} />
      <div className="flex">
        <Button
          className="ml-auto"
          variant="primary"
          onClick={() => onNext({ kind: 'emoji', emoji, platformLinkId: null })}
        >
          {t('actions.continue')}
        </Button>
      </div>
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
  const { t } = useTranslation(['onboarding', 'profile'])
  const [timeZone, setTimeZone] = useState(
    value ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
  )
  const [invalid, setInvalid] = useState(false)
  return (
    <form
      className="flex flex-col gap-5"
      noValidate
      onSubmit={(event) => {
        event.preventDefault()
        if (!isKnownTimeZone(timeZone)) {
          setInvalid(true)
          return
        }
        setInvalid(false)
        onNext(timeZone)
      }}
    >
      <TimeZoneField
        errorMessage={invalid ? t('timeZoneField.invalid', { ns: 'profile' }) : undefined}
        hint={t('timeZoneField.hint', { ns: 'profile' })}
        id="time-zone"
        label={t('fields.timeZone')}
        name="timeZone"
        onChange={(event) => setTimeZone(event.target.value)}
        value={timeZone}
      />
      <div className="flex">
        <Button className="ml-auto" type="submit" variant="primary">
          {t('actions.continue')}
        </Button>
      </div>
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
  // The onboarding-finish "sticker slap" (design §3): this section only
  // mounts once activation lands on 'ready', so its own mount is the finish
  // moment. Scale settles 1.06 -> 1 over 300ms, motion-safe only.
  const [settled, setSettled] = useState(false)
  useEffect(() => {
    const frame = requestAnimationFrame(() => setSettled(true))
    return () => cancelAnimationFrame(frame)
  }, [])

  return (
    <div className="flex flex-col gap-8">
      <p className="max-w-prose text-ink-soft">{t('onboarding.body')}</p>
      {continuation && (
        <Section
          title={t('onboarding.continuation')}
          titleId="continued-profile-heading"
        >
          <ContinuationCard continuation={continuation} />
        </Section>
      )}
      <SectionCard
        title={t('sections.findByCode')}
        titleId="find-by-code-heading"
      >
        <FriendCodeInput
          onResolve={(friendCode) => navigate('/add/' + friendCode)}
        />
      </SectionCard>
      <ShareCard />
      <Section
        className="gap-3"
        title={t('pages.importSteam')}
        titleId="steam-onboarding-heading"
      >
        <p>{t('onboarding.steamOptional')}</p>
        <Link className="font-semibold text-ink underline" to="/friends/import/steam">
          {t('actions.importSteam')}
        </Link>
      </Section>
      {/* The finish slap ships as-is (§3.6): `scale-[1.06]` sits between the
          `scale-105`/`scale-110` steps, and `transition-[scale,box-shadow]`
          names the two animated properties — neither has a utility. */}
      <div
        className={`flex flex-wrap items-center justify-center gap-3 rounded-card border-2 border-ink bg-card p-5 motion-safe:transition-[scale,box-shadow] motion-safe:duration-300 motion-safe:ease-out ${
          settled ? 'scale-100 shadow-offset-card' : 'scale-[1.06] shadow-offset-dialog'
        }`}
      >
        <Button variant="primary" onClick={() => navigate('/app', { replace: true })}>
          {t('actions.finishSetup')}
        </Button>
        <Button variant="neutral" onClick={() => navigate('/app', { replace: true })}>
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

  const currentIndex = steps.indexOf(current)
  // The friends step hosts its own cards (ShareCard, find-by-code), and a
  // bordered card never contains another (design §3.1), so only the five form
  // steps sit inside the wizard card.
  const inCard = current !== 'friends'

  const wizardHeader = (
    <div className="flex flex-col items-center gap-3">
      <ol
        aria-label={t('onboarding.progress')}
        className="flex items-center gap-2"
      >
        {steps.map((step, index) => (
          <li
            key={step}
            aria-current={index === currentIndex ? 'step' : undefined}
            className={`h-3 w-3 rounded-full border-2 border-ink ${
              index <= currentIndex ? 'bg-primary' : 'bg-page'
            } ${
              index === currentIndex
                ? 'outline-2 outline-offset-2 outline-accent'
                : ''
            }`}
          />
        ))}
      </ol>
      <p aria-live="polite" className="font-body text-meta text-ink-soft">
        {t('progress', {
          current: currentIndex + 1,
          total: steps.length,
          step: t(labels[current]),
        })}
      </p>
      <PageHeader
        key={current}
        align="center"
        className="w-full"
        title={t(labels[current])}
      />
    </div>
  )

  const wizardBody = (
    <>
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
        <div className="flex flex-col gap-5">
          <p>{t('messages.platformLinksOptional')}</p>
          {complete.error && (
            <StatusBanner role="alert" variant="danger">
              {errorText(complete.error, (key, options) =>
                t(key, { ns: 'errors', ...options }),
              )}
            </StatusBanner>
          )}
          <div className="flex">
            <Button
              aria-busy={complete.isPending}
              className="ml-auto"
              disabled={complete.isPending}
              variant="primary"
              onClick={() => void activate()}
            >
              {t('actions.continue')}
            </Button>
          </div>
        </div>
      )}
      {current === 'friends' && activation === 'refreshing' && (
        <p role="status">{t('friends:status.loading')}</p>
      )}
      {current === 'friends' && activation === 'error' && (
        <Section
          title={t('friends:onboarding.refreshFailed')}
          titleId="activation-refresh-error"
        >
          <StatusBanner
            actions={
              <Button
                variant="neutral"
                onClick={() => void refreshActivatedSession()}
              >
                {t('friends:actions.retry')}
              </Button>
            }
            role="alert"
            variant="danger"
          >
            {errorText(refreshError, (key, options) =>
              t(key, { ns: 'errors', ...options }),
            )}
          </StatusBanner>
        </Section>
      )}
      {current === 'friends' && activation === 'ready' && (
        <FriendsEducation continuation={continuation} />
      )}
    </>
  )

  return (
    <main data-testid="screen-onboarding">
      <PageScaffold className="items-center" width="form">
        <BrandMark className="h-9 w-9 text-ink" />
        {inCard ? (
          <div className={`${formShellClasses} w-full sm:p-8`}>
            {wizardHeader}
            {wizardBody}
          </div>
        ) : (
          <div className="flex w-full flex-col gap-8">
            {wizardHeader}
            {wizardBody}
          </div>
        )}
      </PageScaffold>
    </main>
  )
}
