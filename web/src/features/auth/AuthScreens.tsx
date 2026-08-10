import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useNavigate } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import {
  ApiError,
  client,
  isApiError,
  refreshAntiforgeryToken,
} from '../../api/client'
import { Button } from '../../components/ui/button'
import { Checkbox } from '../../components/ui/checkbox'
import { raisedInteractiveClasses } from '../../components/ui/classes'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
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
    <p role="alert">
      {t(isApiError(error) ? error.code : 'generic', {
        defaultValue: t('generic'),
      })}
    </p>
  ) : null
}

const linkButtonBase = `inline-flex items-center justify-center gap-2 px-4 py-2 ${raisedInteractiveClasses}`

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
      className="mx-auto flex min-h-[80vh] max-w-3xl flex-col justify-center gap-8 p-4 sm:p-8"
      data-testid="screen-welcome"
    >
      <div className="grid items-center gap-10 sm:grid-cols-[3fr_2fr]">
        <div className="space-y-6">
          <span className="inline-flex items-center rounded-full border-2 border-ink bg-accent px-3 py-1 font-display text-xs font-bold uppercase tracking-widest text-accent-ink dark:border-accent-ink">
            {t('pages.welcome')}
          </span>
          <h1 className="max-w-[16ch] font-display text-4xl leading-[1.05] font-extrabold text-ink sm:text-5xl">
            {t('app.tagline')}
          </h1>
          {showLoginOptions ? (
            <div className="flex flex-wrap gap-3">
              {session?.providers.includes('google') && (
                <Button variant="neutral" onClick={() => void external('google')}>
                  {t('providers.google', { ns: 'auth' })}
                </Button>
              )}
              {session?.providers.includes('steam') && (
                <Button variant="neutral" onClick={() => void external('steam')}>
                  {t('providers.steam', { ns: 'auth' })}
                </Button>
              )}
              <Link className={`${linkButtonBase} bg-primary text-primary-ink`} to="/login">
                {t('providers.email', { ns: 'auth' })}
              </Link>
              <Link className={`${linkButtonBase} bg-card text-ink`} to="/register">
                {t('pages.register', { ns: 'auth' })}
              </Link>
            </div>
          ) : (
            <div className="flex flex-wrap gap-3">
              <Button
                aria-expanded={false}
                variant="primary"
                onClick={() => setShowLoginOptions(true)}
              >
                {t('pages.login', { ns: 'auth' })}
              </Button>
              <Link className={`${linkButtonBase} bg-card text-ink`} to="/register">
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
                className={`h-7 rounded-[0.3rem] border border-ink/15 ${fill} ${
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-age-gate"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('ageGate.title')}
      </h1>
      <div className="flex items-center gap-2">
        <Checkbox.Root
          id="age-gate"
          checked={checked}
          onCheckedChange={(value: boolean | 'indeterminate') =>
            setChecked(value === true)
          }
        />
        <Label htmlFor="age-gate">{t('ageGate.attestation')}</Label>
      </div>
      {attempted && !checked && <p role="alert">{t('ageGate.required')}</p>}
      <Button variant="primary" onClick={continueGate}>
        {t('common.continue', { ns: 'common' })}
      </Button>
    </main>
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-login"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('pages.login')}
      </h1>
      <form className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card" noValidate onSubmit={submit}>
        <div>
          <Label htmlFor="login-email">{t('fields.email')}</Label>
          <Input
            id="login-email"
            type="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            aria-invalid={emailError ? true : undefined}
            aria-describedby={emailError ? 'login-email-error' : undefined}
            required
          />
          {emailError && (
            <p id="login-email-error" role="alert">
              {t(emailError === 'required' ? 'fields.emailRequired' : 'fields.emailInvalid')}
            </p>
          )}
        </div>
        <div>
          <Label htmlFor="login-password">{t('fields.password')}</Label>
          <Input
            id="login-password"
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            aria-invalid={passwordMissing || undefined}
            aria-describedby={passwordMissing ? 'login-password-error' : undefined}
            required
          />
          {passwordMissing && <p id="login-password-error" role="alert">{t('fields.passwordRequired')}</p>}
        </div>
        <Button type="submit" variant="primary">
          {t('actions.signIn')}
        </Button>
      </form>
      <Button variant="neutral" onClick={() => void magic()}>
        {t('actions.emailLink')}
      </Button>
      {sent && <p role="status">{t('messages.checkInbox')}</p>}
      <div className="flex flex-wrap gap-3">
        {session?.providers.includes('google') && (
          <Button variant="neutral" onClick={() => void external('google')}>
            {t('providers.google')}
          </Button>
        )}
        {session?.providers.includes('steam') && (
          <Button variant="neutral" onClick={() => void external('steam')}>
            {t('providers.steam')}
          </Button>
        )}
      </div>
      <ErrorAlert error={error} />
    </main>
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-register"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('pages.register')}
      </h1>
      {sent ? (
        <p role="status">{t('messages.checkInbox')}</p>
      ) : (
        <form className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card" noValidate onSubmit={submit}>
          <div>
            <Label htmlFor="register-email">{t('fields.email')}</Label>
            <Input
              id="register-email"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              aria-invalid={emailError ? true : undefined}
              aria-describedby={emailError ? 'register-email-error' : undefined}
              required
            />
            {emailError && (
              <p id="register-email-error" role="alert">
                {t(emailError === 'required' ? 'fields.emailRequired' : 'fields.emailInvalid')}
              </p>
            )}
          </div>
          <Button type="submit" variant="primary">
            {t('actions.createAccount')}
          </Button>
        </form>
      )}
      <ErrorAlert error={error} />
    </main>
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-auth-magic"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('pages.magic')}
      </h1>
      {!authData.token && <p role="alert">{t('messages.invalidLink')}</p>}
      <ErrorAlert error={error} />
    </main>
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-auth-callback"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('pages.callback')}
      </h1>
      <ErrorAlert error={error} />
    </main>
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid={'screen-auth-' + page}
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('pages.' + page)}
      </h1>
      {!authData.token ? (
        <p role="alert">{t('messages.invalidLink')}</p>
      ) : (
        <form className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card" noValidate onSubmit={submit}>
          {password && (
            <div>
              <Label htmlFor={page + '-password'}>{t('fields.password')}</Label>
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
              <p
                id={page + '-password-hint'}
                className="text-body-sm text-ink-soft"
              >
                {t('fields.passwordHint')}
              </p>
              {passwordTooShort && (
                <p id={page + '-password-error'} role="alert">
                  {t('fields.passwordTooShort')}
                </p>
              )}
            </div>
          )}
          <Button type="submit" variant="primary">
            {t('actions.continue')}
          </Button>
        </form>
      )}
      <ErrorAlert error={error} />
    </main>
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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-auth-confirm-identity"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('pages.confirmIdentity')}
      </h1>
      {token ? (
        <p>{t('messages.confirmedIdentity')}</p>
      ) : (
        <p role="alert">{t('messages.invalidLink')}</p>
      )}
    </main>
  )
}
