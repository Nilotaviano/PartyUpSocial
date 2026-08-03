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

const ageGateStorageKey = 'partyup.age-gate-attested'

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

export function Welcome() {
  const { t } = useTranslation(['common', 'auth'])
  return (
    <main data-testid="screen-welcome">
      <h1>{t('pages.welcome')}</h1>
      <p>{t('app.tagline')}</p>
      <Link to="/login">{t('pages.login', { ns: 'auth' })}</Link>
      <Link to="/register">{t('pages.register', { ns: 'auth' })}</Link>
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
    navigate(returnTo, { replace: true })
  }

  return (
    <main data-testid="screen-age-gate">
      <h1>{t('ageGate.title')}</h1>
      <div>
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
      <Button onClick={continueGate}>
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
  const [error, setError] = useState<unknown>()
  const [sent, setSent] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(undefined)
    try {
      const { data } = await client.POST('/api/v1/auth/login', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { email, password },
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
              mode: 'authentication',
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
    <main data-testid="screen-login">
      <h1>{t('pages.login')}</h1>
      <form onSubmit={submit}>
        <Label htmlFor="login-email">{t('fields.email')}</Label>
        <Input
          id="login-email"
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          required
        />
        <Label htmlFor="login-password">{t('fields.password')}</Label>
        <Input
          id="login-password"
          type="password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          required
        />
        <Button type="submit">{t('actions.signIn')}</Button>
      </form>
      <Button onClick={() => void magic()}>{t('actions.emailLink')}</Button>
      {sent && <p role="status">{t('messages.checkInbox')}</p>}
      {session?.providers.includes('google') && (
        <Button onClick={() => void external('google')}>
          {t('providers.google')}
        </Button>
      )}
      {session?.providers.includes('steam') && (
        <Button onClick={() => void external('steam')}>
          {t('providers.steam')}
        </Button>
      )}
      <ErrorAlert error={error} />
    </main>
  )
}

export function Register() {
  const { t, i18n } = useTranslation('auth')
  const navigate = useNavigate()
  const location = useLocation()
  const continuation = readFriendContinuation(location.search)
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<unknown>()

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(undefined)
    if (!attested()) {
      const returnTo = withFriendContinuation('/register', continuation)
      navigate('/age-gate?next=' + encodeURIComponent(returnTo))
      return
    }

    try {
      await client.POST('/api/v1/auth/register', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          email,
          ageGateAttested: true,
          locale: i18n.language,
          continuation: continuation ?? null,
        },
      })
      setSent(true)
    } catch (caught) {
      setError(caught)
    }
  }

  return (
    <main data-testid="screen-register">
      <h1>{t('pages.register')}</h1>
      {sent ? (
        <p role="status">{t('messages.checkInbox')}</p>
      ) : (
        <form onSubmit={submit}>
          <Label htmlFor="register-email">{t('fields.email')}</Label>
          <Input
            id="register-email"
            type="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            required
          />
          <Button type="submit">{t('actions.createAccount')}</Button>
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
    <main data-testid="screen-auth-magic">
      <h1>{t('pages.magic')}</h1>
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
      if (new URLSearchParams(location.search).get('status') === 'failed') {
        setError(new ApiError(400, 'callbackFailed'))
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
    <main data-testid="screen-auth-callback">
      <h1>{t('pages.callback')}</h1>
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

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!authData.token) return
    setError(undefined)

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
      } else {
        setError(caught)
      }
    }
  }

  return (
    <main data-testid={'screen-auth-' + page}>
      <h1>{t('pages.' + page)}</h1>
      {!authData.token ? (
        <p role="alert">{t('messages.invalidLink')}</p>
      ) : (
        <form onSubmit={submit}>
          {password && (
            <>
              <Label htmlFor={page + '-password'}>{t('fields.password')}</Label>
              <Input
                id={page + '-password'}
                type="password"
                value={value}
                onChange={(event) => setValue(event.target.value)}
                required
              />
            </>
          )}
          <Button type="submit">{t('actions.continue')}</Button>
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
    <main data-testid="screen-auth-confirm-identity">
      <h1>{t('pages.confirmIdentity')}</h1>
      {token ? (
        <p>{t('messages.confirmedIdentity')}</p>
      ) : (
        <p role="alert">{t('messages.invalidLink')}</p>
      )}
    </main>
  )
}
