import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Mail } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useNavigate } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import {
  ApiError,
  client,
  isApiError,
  refreshAntiforgeryToken,
} from '../../api/client'
import { BrandMark } from '../../components/ui/brandMark'
import { Button } from '../../components/ui/button'
import { Checkbox } from '../../components/ui/checkbox'
import { neutralCtaClasses, primaryCtaClasses } from '../../components/ui/classes'
import { FormField, FormShell, formShellClasses } from '../../components/ui/formShell'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { StatusBanner } from '../../components/ui/statusBanner'
import { startExternalProviderWithRetry } from '../../auth/externalIntent'
import {
  readAuthNext,
  readFriendContinuation,
  validateAuthNext,
  validateFriendContinuation,
  withFriendContinuation,
} from '../../auth/friendContinuation'
import { setReauthProof } from '../../auth/reauthProof'
import { peekPendingSettingsAction } from '../settings/reauthAction'
import { sessionQueryKey, useSession } from '../../auth/useSession'
import { PASSWORD_MIN_LENGTH } from '../../auth/passwordPolicy'

const ageGateStorageKey = 'partyup.age-gate-attested'
const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

type EmailFieldError = 'required' | 'invalid' | null

function validateEmailField(value: string): EmailFieldError {
  if (!value) return 'required'
  if (!emailPattern.test(value)) return 'invalid'
  return null
}

type HashAuthData = {
  token: string | null
  continuation: string | undefined
}

type DestinationSession = {
  state: string | null
  onboarding: unknown
  continuation?: string | null
}

let pendingMagicLink: HashAuthData | null = null
let pendingEmailVerification: HashAuthData | null = null
let pendingRegistration: {
  email: string
  continuation: string | undefined
} | null = null

function readHashAuthData(): HashAuthData {
  const parameters = new URLSearchParams(window.location.hash.slice(1))
  const result = {
    token: parameters.get('token'),
    continuation: validateFriendContinuation(parameters.get('next')),
  }
  window.history.replaceState(
    window.history.state,
    '',
    window.location.pathname + window.location.search,
  )
  return result
}

function readHashToken() {
  return readHashAuthData().token
}

function attested() {
  return sessionStorage.getItem(ageGateStorageKey) === 'true'
}

function destination(
  session: DestinationSession | undefined,
  fallback?: string,
) {
  const continuation =
    validateFriendContinuation(session?.continuation) ??
    validateAuthNext(fallback)
  if (session?.state === 'onboarding' || session?.onboarding !== null) {
    return withFriendContinuation(
      '/onboarding',
      validateFriendContinuation(continuation),
    )
  }

  return continuation ?? '/app'
}

function safeAgeGateReturnTo(value: string | undefined) {
  if (
    !value ||
    (!value.startsWith('/register') &&
      !value.startsWith('/login') &&
      !value.startsWith('/auth/magic') &&
      !value.startsWith('/auth/verify-email'))
  ) {
    return '/register'
  }

  const parsed = new URL(value, window.location.origin)
  if (parsed.origin !== window.location.origin || parsed.hash) {
    return '/register'
  }

  const allowedPath = [
    '/register',
    '/login',
    '/auth/magic',
    '/auth/verify-email',
  ].includes(parsed.pathname)
  if (!allowedPath) {
    return '/register'
  }

  return withFriendContinuation(
    parsed.pathname,
    readFriendContinuation(parsed.search),
  )
}

function ErrorAlert({ error }: { error: unknown }) {
  const { t } = useTranslation('errors')
  return error ? (
    <StatusBanner role="alert" variant="danger">
      {t(isApiError(error) ? error.code : 'generic', {
        defaultValue: t('generic'),
      })}
    </StatusBanner>
  ) : null
}

/**
 * The shared auth column (STYLING_OVERHAUL_DESIGN.md §3.6): brand mark, then a
 * centred `max-w-form` stack. The h1 comes from PageHeader, so every auth
 * screen carries the §1 focus-on-mount contract the rest of the app has.
 */
function AuthScreen({
  testId,
  title,
  children,
}: {
  testId: string
  title: string
  children: ReactNode
}) {
  return (
    <main data-testid={testId}>
      <PageScaffold className="items-center" width="form">
        <BrandMark className="h-9 w-9 text-ink" />
        <div className="flex w-full flex-col gap-6">
          <PageHeader align="center" title={title} />
          {children}
        </div>
      </PageScaffold>
    </main>
  )
}

// The sign-in-methods row grammar (owner-approved Lovable design): full-width
// h-12 rounded-xl rows. `authRowOutlineClasses` sits on a `surface` (`bg-card`)
// panel here and on /login, distinguishing it from `AccountMenu.tsx`'s
// `rowClasses`, which is `bg-card` itself because its rows sit directly on
// the sheet's page background rather than inside a nested panel.
const authRowOutlineClasses =
  'flex h-12 w-full cursor-pointer items-center justify-center gap-2 rounded-xl border border-border bg-background px-4 text-center font-display text-[14px] font-bold text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const authRowPrimaryClasses =
  'flex h-12 w-full cursor-pointer items-center justify-center gap-2 rounded-xl bg-primary px-4 font-display text-[14px] font-bold text-primary-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'
const authFieldClasses =
  'h-12 w-full rounded-xl border border-border bg-background px-3 font-body text-[14px] text-foreground placeholder:text-muted-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:opacity-50'
const authFieldLabelClasses = 'font-display text-[13px] font-bold text-foreground'
const quietAuthLinkClasses =
  'inline-flex min-h-11 w-fit cursor-pointer items-center justify-center font-display text-[13px] font-bold text-muted-foreground underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

/**
 * Decoration for the welcome "how it works" band (§3.6): the hero's own
 * vocabulary — outlined time blocks — reduced to a step-sized figure. Purely
 * `aria-hidden`; each step carries its meaning in its title and body.
 */
function HowFigure({ step }: { step: 1 | 2 | 3 }) {
  return (
    <svg
      aria-hidden="true"
      className="h-12 w-12 text-ink"
      fill="none"
      focusable="false"
      viewBox="0 0 48 48"
      xmlns="http://www.w3.org/2000/svg"
    >
      {step === 1 && (
        <>
          <rect className="fill-free" height="12" rx="3" stroke="currentColor" strokeWidth="2.5" width="16" x="4" y="6" />
          <rect className="fill-busy" height="12" rx="3" stroke="currentColor" strokeWidth="2.5" width="16" x="28" y="6" />
          <rect className="fill-busy" height="12" rx="3" stroke="currentColor" strokeWidth="2.5" width="16" x="4" y="30" />
          <rect className="fill-free" height="12" rx="3" stroke="currentColor" strokeWidth="2.5" width="16" x="28" y="30" />
        </>
      )}
      {step === 2 && (
        <>
          <rect className="fill-free" height="20" rx="4" stroke="currentColor" strokeWidth="2.5" width="24" x="4" y="8" />
          <rect className="fill-primary" height="20" rx="4" stroke="currentColor" strokeWidth="2.5" width="24" x="20" y="20" />
        </>
      )}
      {step === 3 && (
        <>
          <rect className="fill-primary" height="28" rx="5" stroke="currentColor" strokeWidth="2.5" width="36" x="6" y="10" />
          <path d="M15 24l6 6 12-12" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="3.5" />
        </>
      )}
    </svg>
  )
}

function HowItWorks() {
  const { t } = useTranslation('auth')

  return (
    <ol className="grid gap-6 sm:grid-cols-3">
      {([1, 2, 3] as const).map((step) => (
        <li
          key={step}
          className="flex flex-col items-start gap-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card"
        >
          <HowFigure step={step} />
          <h2 className="font-display text-h3 font-semibold text-ink">
            {t(`welcome.how.${step}.title`)}
          </h2>
          <p className="font-body text-body text-ink-soft">
            {t(`welcome.how.${step}.body`)}
          </p>
        </li>
      ))}
    </ol>
  )
}

export function Welcome() {
  const { t } = useTranslation(['common', 'auth'])
  const { data: session } = useSession()
  const [showLoginOptions, setShowLoginOptions] = useState(false)
  const [error, setError] = useState<unknown>()

  async function external(provider: string) {
    setError(undefined)
    try {
      await startExternalProviderWithRetry(
        async () => {
          const { data } = await client.POST('/api/v1/auth/external/intent', {
            params: { header: { 'X-CSRF-TOKEN': '' } },
            body: {
              provider,
              mode: 'login',
              returnTo: null,
              ageGateAttested: attested(),
              currentPassword: null,
              reauthToken: null,
            },
          })
          if (!data) throw new ApiError(500, 'request_failed')
          return data
        },
        async (intent) => {
          window.location.assign(
            '/api/v1/auth/external/' +
              provider +
              '/start?intent=' +
              encodeURIComponent(intent),
          )
        },
      )
    } catch (caught) {
      setError(caught)
    }
  }

  return (
    <main
      className="mx-auto flex w-full max-w-page flex-col gap-12 px-4 py-8 sm:px-8 sm:py-12"
      data-testid="screen-welcome"
    >
      <div className="grid items-center gap-10 py-6 sm:grid-cols-[3fr_2fr] sm:py-12">
        <div className="space-y-6">
          <span className="inline-flex items-center gap-2 rounded-full border-2 border-ink bg-accent px-3 py-1 font-display text-xs font-bold uppercase tracking-widest text-accent-ink dark:border-accent-ink">
            <BrandMark className="h-4 w-4" />
            {t('pages.welcome')}
          </span>
          {/* `max-w-[16ch]` caps the tagline's measure in characters, and
              `leading-[1.05]` is the display line-height (`--text-display--line-height`)
              carried onto a hero size the type scale does not name. */}
          <h1 className="max-w-[16ch] font-display text-4xl leading-[1.05] font-extrabold text-ink sm:text-5xl">
            {t('app.tagline')}
          </h1>
          <div className="flex flex-wrap gap-3">
            <Button
              aria-controls="welcome-signin-panel"
              aria-expanded={showLoginOptions}
              variant="primary"
              onClick={() => setShowLoginOptions((expanded) => !expanded)}
            >
              {t('pages.login', { ns: 'auth' })}
            </Button>
            {!showLoginOptions && (
              <Link className={neutralCtaClasses} to="/register">
                {t('pages.register', { ns: 'auth' })}
              </Link>
            )}
          </div>
          {showLoginOptions && (
            <div className="surface flex flex-col gap-3 p-4" id="welcome-signin-panel">
              <p className="font-body text-[13px] text-muted-foreground">
                {t('welcome.signIn.intro', { ns: 'auth' })}
              </p>
              {session?.providers.includes('google') && (
                <button
                  className={authRowOutlineClasses}
                  type="button"
                  onClick={() => void external('google')}
                >
                  {t('providers.google', { ns: 'auth' })}
                </button>
              )}
              {session?.providers.includes('steam') && (
                <button
                  className={authRowOutlineClasses}
                  type="button"
                  onClick={() => void external('steam')}
                >
                  {t('providers.steam', { ns: 'auth' })}
                </button>
              )}
              <Link className={authRowOutlineClasses} to="/login">
                <Mail aria-hidden className="size-4" />
                {t('welcome.signIn.email', { ns: 'auth' })}
              </Link>
              <Link className={authRowPrimaryClasses} to="/register">
                {t('pages.register', { ns: 'auth' })}
              </Link>
            </div>
          )}
          <ErrorAlert error={error} />
        </div>
        {/* Decorative miniature of the product's own week grid — the one
            surface that explains PartyUp without words. Colors are grid
            tokens; the ringed column is the selection band idiom. */}
        <div
          aria-hidden="true"
          className="max-w-sm rotate-2 rounded-card border-2 border-ink bg-card p-4 shadow-offset-card"
        >
          <div className="mb-3 flex items-center gap-2">
            <span className="h-2.5 w-10 rounded-full bg-ink/20" />
            <span className="h-2.5 w-6 rounded-full bg-ink/10" />
          </div>
          <div className="grid grid-cols-5 gap-1">
            {[
              'bg-page', 'bg-free', 'bg-page', 'bg-busy', 'bg-page',
              'bg-busy', 'bg-free', 'bg-free', 'bg-page', 'bg-busy',
              'bg-page', 'bg-free', 'bg-free', 'bg-free', 'bg-page',
              'bg-free', 'bg-free', 'bg-page', 'bg-busy', 'bg-free',
              'bg-page', 'bg-busy', 'bg-page', 'bg-page', 'bg-page',
            ].map((fill, index) => (
              <span
                key={index}
                className={`h-7 rounded-[0.25rem] border border-ink/15 ${fill} ${
                  index === 6 || index === 11 ? 'ring-2 ring-ink ring-inset' : ''
                }`}
              />
            ))}
          </div>
          <div className="mt-3 inline-flex items-center rounded-full border-2 border-ink bg-accent px-2.5 py-0.5 font-display text-xs font-bold text-accent-ink dark:border-accent-ink">
            21:00 — 23:00
          </div>
        </div>
      </div>
      <HowItWorks />
    </main>
  )
}

export function AgeGate() {
  const { t } = useTranslation(['auth', 'common'])
  const navigate = useNavigate()
  const location = useLocation()
  const [checked, setChecked] = useState(attested)
  const [attempted, setAttempted] = useState(false)
  const stateReturnTo = (location.state as { returnTo?: string } | null)
    ?.returnTo
  const queryReturnTo =
    new URLSearchParams(location.search).get('next') ?? undefined
  const returnTo = safeAgeGateReturnTo(stateReturnTo ?? queryReturnTo)

  function continueGate() {
    if (!checked) {
      setAttempted(true)
      return
    }

    sessionStorage.setItem(ageGateStorageKey, 'true')
    navigate(returnTo, { replace: true, state: { justAttested: true } })
  }

  return (
    <AuthScreen testId="screen-age-gate" title={t('ageGate.title')}>
      <div className={`${formShellClasses} sm:p-8`}>
        <div className="flex items-center gap-3">
          <Checkbox.Root
            id="age-gate"
            checked={checked}
            onCheckedChange={(value: boolean | 'indeterminate') =>
              setChecked(value === true)
            }
          />
          <Label htmlFor="age-gate">{t('ageGate.attestation')}</Label>
        </div>
        {attempted && !checked && (
          <StatusBanner role="alert" variant="danger">
            {t('ageGate.required')}
          </StatusBanner>
        )}
        <div className="flex">
          <Button className="ml-auto" variant="primary" onClick={continueGate}>
            {t('common.continue', { ns: 'common' })}
          </Button>
        </div>
      </div>
    </AuthScreen>
  )
}

export function Login() {
  const { t, i18n } = useTranslation('auth')
  const navigate = useNavigate()
  const location = useLocation()
  const queryClient = useQueryClient()
  const { data: session } = useSession()
  const continuation = readFriendContinuation(location.search)
  const authNext = readAuthNext(location.search)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [emailError, setEmailError] = useState<EmailFieldError>(null)
  const [passwordMissing, setPasswordMissing] = useState(false)
  const [error, setError] = useState<unknown>()
  const [sent, setSent] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(undefined)
    const trimmedEmail = email.trim()
    const emailValidation = validateEmailField(trimmedEmail)
    const missingPassword = password.length === 0
    setEmailError(emailValidation)
    setPasswordMissing(missingPassword)
    if (emailValidation || missingPassword) return
    try {
      const { data } = await client.POST('/api/v1/auth/login', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { email: trimmedEmail, password },
      })
      await refreshAntiforgeryToken()
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      navigate(destination(data, authNext), { replace: true })
    } catch (caught) {
      setError(caught)
    }
  }

  async function magic() {
    setError(undefined)
    const emailValidation = validateEmailField(email.trim())
    setEmailError(emailValidation)
    if (emailValidation) return
    try {
      await client.POST('/api/v1/auth/magic-link', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          email,
          locale: i18n.language,
          continuation: continuation ?? null,
        },
      })
      setSent(true)
    } catch (caught) {
      setError(caught)
    }
  }

  async function external(provider: string) {
    setError(undefined)
    try {
      await startExternalProviderWithRetry(
        async () => {
          const { data } = await client.POST('/api/v1/auth/external/intent', {
            params: { header: { 'X-CSRF-TOKEN': '' } },
            body: {
              provider,
              mode: 'login',
              returnTo: authNext ?? null,
              ageGateAttested: attested(),
              currentPassword: null,
              reauthToken: null,
            },
          })
          if (!data) throw new ApiError(500, 'request_failed')
          return data
        },
        async (intent) => {
          window.location.assign(
            '/api/v1/auth/external/' +
              provider +
              '/start?intent=' +
              encodeURIComponent(intent),
          )
        },
      )
    } catch (caught) {
      setError(caught)
    }
  }

  return (
    <AuthScreen testId="screen-login" title={t('pages.login')}>
      <div className="surface flex flex-col gap-4 p-5">
        <form className="flex flex-col gap-4" noValidate onSubmit={submit}>
          <div className="flex flex-col gap-1.5">
            <label className={authFieldLabelClasses} htmlFor="login-email">
              {t('fields.email')}
            </label>
            <input
              className={authFieldClasses}
              id="login-email"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              aria-invalid={emailError ? true : undefined}
              aria-describedby={emailError ? 'login-email-error' : undefined}
              required
            />
            <div className="min-h-[1.03125rem]">
              {emailError && (
                <p className="font-body text-[12px] leading-snug text-danger" id="login-email-error" role="alert">
                  {t(emailError === 'required' ? 'fields.emailRequired' : 'fields.emailInvalid')}
                </p>
              )}
            </div>
          </div>
          <div className="flex flex-col gap-1.5">
            <label className={authFieldLabelClasses} htmlFor="login-password">
              {t('fields.password')}
            </label>
            <input
              className={authFieldClasses}
              id="login-password"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              aria-invalid={passwordMissing || undefined}
              aria-describedby={passwordMissing ? 'login-password-error' : undefined}
              required
            />
            <div className="min-h-[1.03125rem]">
              {passwordMissing && (
                <p className="font-body text-[12px] leading-snug text-danger" id="login-password-error" role="alert">
                  {t('fields.passwordRequired')}
                </p>
              )}
            </div>
          </div>
          <button className={primaryCtaClasses} type="submit">
            {t('actions.signIn')}
          </button>
        </form>
      </div>
      <div className="flex flex-col gap-3">
        <div className="surface flex flex-col gap-3 p-4">
          <button className={authRowOutlineClasses} type="button" onClick={() => void magic()}>
            {t('actions.emailLink')}
          </button>
          {session?.providers.includes('google') && (
            <button className={authRowOutlineClasses} type="button" onClick={() => void external('google')}>
              {t('providers.google')}
            </button>
          )}
          {session?.providers.includes('steam') && (
            <button className={authRowOutlineClasses} type="button" onClick={() => void external('steam')}>
              {t('providers.steam')}
            </button>
          )}
        </div>
        {sent && (
          <StatusBanner role="status" variant="success">
            {t('messages.checkInbox')}
          </StatusBanner>
        )}
        <Link className={quietAuthLinkClasses} to="/register">
          {t('pages.register')}
        </Link>
      </div>
      <ErrorAlert error={error} />
    </AuthScreen>
  )
}

export function Register() {
  const { t, i18n } = useTranslation('auth')
  const navigate = useNavigate()
  const location = useLocation()
  const continuation = readFriendContinuation(location.search)
  const justAttested = Boolean(
    (location.state as { justAttested?: boolean } | null)?.justAttested,
  )
  const [resuming] = useState(() => pendingRegistration)
  const [email, setEmail] = useState(() => resuming?.email ?? '')
  const [emailError, setEmailError] = useState<EmailFieldError>(null)
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<unknown>()

  async function register(
    targetEmail: string,
    targetContinuation: string | undefined,
  ) {
    setError(undefined)
    try {
      await client.POST('/api/v1/auth/register', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          email: targetEmail,
          ageGateAttested: true,
          locale: i18n.language,
          continuation: targetContinuation ?? null,
        },
      })
      setSent(true)
    } catch (caught) {
      setError(caught)
    }
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(undefined)
    const emailValidation = validateEmailField(email.trim())
    setEmailError(emailValidation)
    if (emailValidation) return
    if (!attested()) {
      pendingRegistration = { email, continuation }
      const returnTo = withFriendContinuation('/register', continuation)
      navigate('/age-gate?next=' + encodeURIComponent(returnTo))
      return
    }

    await register(email, continuation)
  }

  useEffect(() => {
    // One-shot: consume the stash immediately so a StrictMode replay or a
    // later mount can never resubmit it. Auto-submit only when this exact
    // navigation returned from the age gate (justAttested) — a stash left
    // over from an abandoned attempt, met by attestation from an unrelated
    // flow, prefills the email field but does not auto-POST. The current
    // location's continuation wins over the stashed one, so a freshly
    // clicked invite link isn't overridden by a stale stash.
    const stash = pendingRegistration
    pendingRegistration = null
    void (async () => {
      if (stash && justAttested) {
        await register(stash.email, continuation ?? stash.continuation)
      }
    })()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <AuthScreen testId="screen-register" title={t('pages.register')}>
      {sent ? (
        <StatusBanner role="status" variant="success">
          {t('messages.checkInbox')}
        </StatusBanner>
      ) : (
        <div className="surface flex flex-col gap-4 p-5">
          <form className="flex flex-col gap-4" noValidate onSubmit={submit}>
            <div className="flex flex-col gap-1.5">
              <label className={authFieldLabelClasses} htmlFor="register-email">
                {t('fields.email')}
              </label>
              <input
                className={authFieldClasses}
                id="register-email"
                type="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                aria-invalid={emailError ? true : undefined}
                aria-describedby={emailError ? 'register-email-error' : undefined}
                required
              />
              <div className="min-h-[1.03125rem]">
                {emailError && (
                  <p className="font-body text-[12px] leading-snug text-danger" id="register-email-error" role="alert">
                    {t(emailError === 'required' ? 'fields.emailRequired' : 'fields.emailInvalid')}
                  </p>
                )}
              </div>
            </div>
            <p className="font-body text-[13px] text-muted-foreground">
              {t('register.explainer')}
            </p>
            <button className={primaryCtaClasses} type="submit">
              {t('actions.createAccount')}
            </button>
          </form>
        </div>
      )}
      {!sent && (
        <Link className={quietAuthLinkClasses} to="/login">
          {t('pages.login')}
        </Link>
      )}
      <ErrorAlert error={error} />
    </AuthScreen>
  )
}

export function MagicLink() {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [authData] = useState(() => pendingMagicLink ?? readHashAuthData())
  const [error, setError] = useState<unknown>()

  useEffect(() => {
    if (!authData.token) return

    void (async () => {
      try {
        const { data } = await client.POST('/api/v1/auth/magic-link/consume', {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: { token: authData.token!, ageGateAttested: attested() },
        })
        pendingMagicLink = null
        await refreshAntiforgeryToken()
        await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
        navigate(destination(data), { replace: true })
      } catch (caught) {
        if (
          isApiError(caught) &&
          caught.status === 409 &&
          caught.code === 'age_gate_required'
        ) {
          pendingMagicLink = authData
          navigate('/age-gate', {
            replace: true,
            state: {
              returnTo: withFriendContinuation(
                '/auth/magic',
                authData.continuation,
              ),
            },
          })
        } else {
          setError(caught)
        }
      }
    })()
  }, [authData, navigate, queryClient])

  return (
    <AuthScreen testId="screen-auth-magic" title={t('pages.magic')}>
      {!authData.token && (
        <StatusBanner role="alert" variant="danger">
          {t('messages.invalidLink')}
        </StatusBanner>
      )}
      <ErrorAlert error={error} />
    </AuthScreen>
  )
}

export function AuthCallback() {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const location = useLocation()
  const queryClient = useQueryClient()
  const [reauth] = useState(() => {
    const value = new URLSearchParams(window.location.hash.slice(1)).get(
      'reauth',
    )
    window.history.replaceState(
      window.history.state,
      '',
      window.location.pathname + window.location.search,
    )
    if (value) setReauthProof(value)
    return value
  })
  const [error, setError] = useState<unknown>()

  useEffect(() => {
    void (async () => {
      const status = new URLSearchParams(location.search).get('status')
      if (status === 'failed') {
        setError(new ApiError(400, 'callbackFailed'))
        return
      }

      if (status === 'age_gate_required') {
        navigate('/age-gate', {
          replace: true,
          state: {
            returnTo: withFriendContinuation(
              '/login',
              readAuthNext(location.search),
            ),
          },
        })
        return
      }

      try {
        await refreshAntiforgeryToken()
        const result = await queryClient.fetchQuery({
          queryKey: sessionQueryKey,
          queryFn: async () => {
            const { data } = await client.GET('/api/v1/auth/session')
            if (!data) throw new ApiError(500, 'request_failed')
            return data
          },
        })
        const reauthDestination =
          peekPendingSettingsAction()?.kind === 'unlinkPlatform'
            ? '/settings/profile'
            : '/settings/account'
        navigate(
          reauth
            ? reauthDestination
            : destination(result, readAuthNext(location.search)),
          { replace: true },
        )
      } catch (caught) {
        setError(caught)
      }
    })()
  }, [location.search, navigate, queryClient, reauth])

  return (
    <AuthScreen testId="screen-auth-callback" title={t('pages.callback')}>
      <ErrorAlert error={error} />
    </AuthScreen>
  )
}

type TokenPage = 'reset' | 'verifyEmail' | 'emailChange'
type TokenEndpoint =
  | '/api/v1/auth/password/reset'
  | '/api/v1/auth/email/verify'
  | '/api/v1/auth/email/change/verify'

function TokenForm({
  page,
  endpoint,
  password = false,
}: {
  page: TokenPage
  endpoint: TokenEndpoint
  password?: boolean
}) {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [authData] = useState(() =>
    page === 'verifyEmail' && pendingEmailVerification
      ? pendingEmailVerification
      : readHashAuthData(),
  )
  const [value, setValue] = useState('')
  const [error, setError] = useState<unknown>()
  const [passwordTooShort, setPasswordTooShort] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!authData.token) return
    setError(undefined)
    setPasswordTooShort(false)
    if (password && value.length < PASSWORD_MIN_LENGTH) {
      setPasswordTooShort(true)
      return
    }

    try {
      let nextDestination = '/'
      if (endpoint === '/api/v1/auth/email/verify') {
        const { data } = await client.POST(endpoint, {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: {
            token: authData.token,
            password: value,
            ageGateAttested: attested(),
          },
        })
        pendingEmailVerification = null
        nextDestination = destination(data)
      } else if (endpoint === '/api/v1/auth/password/reset') {
        await client.POST(endpoint, {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: { token: authData.token, newPassword: value },
        })
      } else {
        await client.POST(endpoint, {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: { token: authData.token },
        })
      }

      await refreshAntiforgeryToken()
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      navigate(nextDestination, { replace: true })
    } catch (caught) {
      if (
        page === 'verifyEmail' &&
        isApiError(caught) &&
        caught.status === 409 &&
        caught.code === 'age_gate_required'
      ) {
        pendingEmailVerification = authData
        navigate('/age-gate', {
          replace: true,
          state: {
            returnTo: withFriendContinuation(
              '/auth/verify-email',
              authData.continuation,
            ),
          },
        })
      } else if (
        password &&
        isApiError(caught) &&
        caught.code === 'auth_validation'
      ) {
        setPasswordTooShort(true)
      } else {
        setError(caught)
      }
    }
  }

  return (
    <AuthScreen testId={'screen-auth-' + page} title={t('pages.' + page)}>
      {!authData.token ? (
        <StatusBanner role="alert" variant="danger">
          {t('messages.invalidLink')}
        </StatusBanner>
      ) : (
        <FormShell
          onSubmit={submit}
          submit={
            <Button type="submit" variant="primary">
              {t('actions.continue')}
            </Button>
          }
        >
          {password && (
            <FormField
              hint={t('fields.passwordHint')}
              htmlFor={page + '-password'}
              label={t('fields.password')}
            >
              <Input
                id={page + '-password'}
                type="password"
                value={value}
                onChange={(event) => setValue(event.target.value)}
                minLength={PASSWORD_MIN_LENGTH}
                aria-describedby={
                  passwordTooShort
                    ? `${page}-password-hint ${page}-password-error`
                    : page + '-password-hint'
                }
                aria-invalid={passwordTooShort || undefined}
                required
              />
              {passwordTooShort && (
                <p id={page + '-password-error'} role="alert">
                  {t('fields.passwordTooShort')}
                </p>
              )}
            </FormField>
          )}
        </FormShell>
      )}
      <ErrorAlert error={error} />
    </AuthScreen>
  )
}

export function ResetPassword() {
  return (
    <TokenForm page="reset" endpoint="/api/v1/auth/password/reset" password />
  )
}

export function VerifyEmail() {
  return (
    <TokenForm
      page="verifyEmail"
      endpoint="/api/v1/auth/email/verify"
      password
    />
  )
}

export function EmailChange() {
  return (
    <TokenForm page="emailChange" endpoint="/api/v1/auth/email/change/verify" />
  )
}

export function ConfirmIdentity() {
  const { t } = useTranslation('auth')
  const navigate = useNavigate()
  const [token] = useState(() => {
    const value = readHashToken()
    if (value) setReauthProof(value)
    return value
  })

  useEffect(() => {
    if (token) {
      navigate(
        peekPendingSettingsAction()?.kind === 'unlinkPlatform'
          ? '/settings/profile'
          : '/settings/account',
        { replace: true, state: { identityConfirmed: true } },
      )
    }
  }, [navigate, token])

  return (
    <AuthScreen
      testId="screen-auth-confirm-identity"
      title={t('pages.confirmIdentity')}
    >
      {token ? (
        <StatusBanner variant="success">
          {t('messages.confirmedIdentity')}
        </StatusBanner>
      ) : (
        <StatusBanner role="alert" variant="danger">
          {t('messages.invalidLink')}
        </StatusBanner>
      )}
    </AuthScreen>
  )
}
