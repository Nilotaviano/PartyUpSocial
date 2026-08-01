import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useNavigate } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError, client, isApiError, refreshAntiforgeryToken } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Checkbox } from '../../components/ui/checkbox'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import { startExternalProviderWithRetry } from '../../auth/externalIntent'
import { setReauthProof } from '../../auth/reauthProof'
import { getPendingSettingsAction } from '../settings/reauthAction'
import { sessionQueryKey, useSession } from '../../auth/useSession'

const ageGateStorageKey = 'partyup.age-gate-attested'
let pendingMagicToken: string | null = null
function readHashToken() { const token = new URLSearchParams(window.location.hash.slice(1)).get('token'); window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search); return token }
function attested() { return sessionStorage.getItem(ageGateStorageKey) === 'true' }
function next(search: string) { return new URLSearchParams(search).get('next') ?? undefined }
function destination(session: { state: string | null; onboarding: unknown } | undefined) { return session?.state === 'onboarding' || session?.onboarding !== null ? '/onboarding' : '/app' }
function ErrorAlert({ error }: { error: unknown }) { const { t } = useTranslation('errors'); return error ? <p role="alert">{t(isApiError(error) ? error.code : 'generic', { defaultValue: t('generic') })}</p> : null }

export function Welcome() { const { t } = useTranslation(['common', 'auth']); return <main data-testid="screen-welcome"><h1>{t('pages.welcome')}</h1><p>{t('app.tagline')}</p><Link to="/login">{t('pages.login', { ns: 'auth' })}</Link><Link to="/register">{t('pages.register', { ns: 'auth' })}</Link></main> }

export function AgeGate() {
  const { t } = useTranslation(['auth', 'common']); const navigate = useNavigate(); const location = useLocation(); const [checked, setChecked] = useState(attested); const [attempted, setAttempted] = useState(false)
  const returnTo = (location.state as { returnTo?: string } | null)?.returnTo ?? next(location.search) ?? '/register'
  function continueGate() { if (!checked) { setAttempted(true); return }; sessionStorage.setItem(ageGateStorageKey, 'true'); navigate(returnTo, { replace: true }) }
  return <main data-testid="screen-age-gate"><h1>{t('ageGate.title')}</h1><div><Checkbox.Root id="age-gate" checked={checked} onCheckedChange={(value: boolean | 'indeterminate') => setChecked(value === true)} /><Label htmlFor="age-gate">{t('ageGate.attestation')}</Label></div>{attempted && !checked && <p role="alert">{t('ageGate.required')}</p>}<Button onClick={continueGate}>{t('common.continue', { ns: 'common' })}</Button></main>
}

export function Login() {
  const { t, i18n } = useTranslation('auth'); const navigate = useNavigate(); const location = useLocation(); const queryClient = useQueryClient(); const { data: session } = useSession(); const [email, setEmail] = useState(''); const [password, setPassword] = useState(''); const [error, setError] = useState<unknown>(); const [sent, setSent] = useState(false)
  async function submit(event: FormEvent) { event.preventDefault(); setError(undefined); try { const { data } = await client.POST('/api/v1/auth/login', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { email, password } }); await refreshAntiforgeryToken(); await queryClient.invalidateQueries({ queryKey: sessionQueryKey }); navigate(next(location.search) ?? destination(data), { replace: true }) } catch (caught) { setError(caught) } }
  async function magic() { setError(undefined); try { await client.POST('/api/v1/auth/magic-link', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { email, locale: i18n.language } }); setSent(true) } catch (caught) { setError(caught) } }
  async function external(provider: string) { setError(undefined); try { await startExternalProviderWithRetry(async () => { const { data } = await client.POST('/api/v1/auth/external/intent', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { provider, mode: 'authentication', returnTo: next(location.search) ?? null, ageGateAttested: attested(), currentPassword: null, reauthToken: null } }); if (!data) throw new ApiError(500, 'request_failed'); return data }, async (intent) => { window.location.assign('/api/v1/auth/external/' + provider + '/start?intent=' + encodeURIComponent(intent)) }) } catch (caught) { setError(caught) } }
  return <main data-testid="screen-login"><h1>{t('pages.login')}</h1><form onSubmit={submit}><Label htmlFor="login-email">{t('fields.email')}</Label><Input id="login-email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} required /><Label htmlFor="login-password">{t('fields.password')}</Label><Input id="login-password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} required /><Button type="submit">{t('actions.signIn')}</Button></form><Button onClick={() => void magic()}>{t('actions.emailLink')}</Button>{sent && <p role="status">{t('messages.checkInbox')}</p>}{session?.providers.includes('google') && <Button onClick={() => void external('google')}>{t('providers.google')}</Button>}{session?.providers.includes('steam') && <Button onClick={() => void external('steam')}>{t('providers.steam')}</Button>}<ErrorAlert error={error} /></main>
}

export function Register() {
  const { t, i18n } = useTranslation('auth'); const navigate = useNavigate(); const [email, setEmail] = useState(''); const [sent, setSent] = useState(false); const [error, setError] = useState<unknown>()
  async function submit(event: FormEvent) { event.preventDefault(); setError(undefined); if (!attested()) { navigate('/age-gate?next=%2Fregister'); return }; try { await client.POST('/api/v1/auth/register', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { email, ageGateAttested: true, locale: i18n.language } }); setSent(true) } catch (caught) { setError(caught) } }
  return <main data-testid="screen-register"><h1>{t('pages.register')}</h1>{sent ? <p role="status">{t('messages.checkInbox')}</p> : <form onSubmit={submit}><Label htmlFor="register-email">{t('fields.email')}</Label><Input id="register-email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} required /><Button type="submit">{t('actions.createAccount')}</Button></form>}<ErrorAlert error={error} /></main>
}

export function MagicLink() {
  const { t } = useTranslation('auth'); const navigate = useNavigate(); const queryClient = useQueryClient(); const [token] = useState(() => pendingMagicToken ?? readHashToken()); const [error, setError] = useState<unknown>()
  useEffect(() => { if (!token) return; void (async () => { try { const { data } = await client.POST('/api/v1/auth/magic-link/consume', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { token, ageGateAttested: attested() } }); pendingMagicToken = null; await refreshAntiforgeryToken(); await queryClient.invalidateQueries({ queryKey: sessionQueryKey }); navigate(destination(data), { replace: true }) } catch (caught) { if (isApiError(caught) && caught.status === 409 && caught.code === 'age_gate_required') { pendingMagicToken = token; navigate('/age-gate', { replace: true, state: { returnTo: '/auth/magic' } }) } else setError(caught) } })() }, [navigate, queryClient, token])
  return <main data-testid="screen-auth-magic"><h1>{t('pages.magic')}</h1>{!token && <p role="alert">{t('messages.invalidLink')}</p>}<ErrorAlert error={error} /></main>
}

export function AuthCallback() {
  const { t } = useTranslation('auth'); const navigate = useNavigate(); const location = useLocation(); const queryClient = useQueryClient(); const [reauth] = useState(() => { const value = new URLSearchParams(window.location.hash.slice(1)).get('reauth'); window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search); if (value) setReauthProof(value); return value }); const [error, setError] = useState<unknown>()
  useEffect(() => { void (async () => { if (new URLSearchParams(location.search).get('status') === 'failed') { setError(new ApiError(400, 'callbackFailed')); return } try { await refreshAntiforgeryToken(); const result = await queryClient.fetchQuery({ queryKey: sessionQueryKey, queryFn: async () => { const { data } = await client.GET('/api/v1/auth/session'); if (!data) throw new ApiError(500, 'request_failed'); return data } }); navigate(next(location.search) ?? (reauth ? '/settings/account' : destination(result)), { replace: true }) } catch (caught) { setError(caught) } })() }, [location.search, navigate, queryClient, reauth])
  return <main data-testid="screen-auth-callback"><h1>{t('pages.callback')}</h1><ErrorAlert error={error} /></main>
}

function TokenForm({ page, endpoint, password = false }: { page: 'reset' | 'verifyEmail' | 'emailChange'; endpoint: '/api/v1/auth/password/reset' | '/api/v1/auth/email/verify' | '/api/v1/auth/email/change/verify'; password?: boolean }) {
  const { t } = useTranslation('auth'); const navigate = useNavigate(); const queryClient = useQueryClient(); const [token] = useState(readHashToken); const [value, setValue] = useState(''); const [error, setError] = useState<unknown>()
  async function submit(event: FormEvent) { event.preventDefault(); if (!token) return; setError(undefined); try { if (endpoint === '/api/v1/auth/email/verify') await client.POST(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { token, password: value, ageGateAttested: attested() } }); else if (endpoint === '/api/v1/auth/password/reset') await client.POST(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { token, newPassword: value } }); else await client.POST(endpoint, { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { token } }); await refreshAntiforgeryToken(); await queryClient.invalidateQueries({ queryKey: sessionQueryKey }); navigate('/') } catch (caught) { setError(caught) } }
  return <main data-testid={'screen-auth-' + page}><h1>{t('pages.' + page)}</h1>{!token ? <p role="alert">{t('messages.invalidLink')}</p> : <form onSubmit={submit}>{password && <><Label htmlFor={page + '-password'}>{t('fields.password')}</Label><Input id={page + '-password'} type="password" value={value} onChange={(event) => setValue(event.target.value)} required /></>}<Button type="submit">{t('actions.continue')}</Button></form>}<ErrorAlert error={error} /></main>
}
export function ResetPassword() { return <TokenForm page="reset" endpoint="/api/v1/auth/password/reset" password /> }
export function VerifyEmail() { return <TokenForm page="verifyEmail" endpoint="/api/v1/auth/email/verify" password /> }
export function EmailChange() { return <TokenForm page="emailChange" endpoint="/api/v1/auth/email/change/verify" /> }
export function ConfirmIdentity() { const { t } = useTranslation('auth'); const navigate = useNavigate(); const [token] = useState(() => { const value = readHashToken(); if (value) setReauthProof(value); return value }); const hasPendingAction = getPendingSettingsAction() !== null; return <main data-testid="screen-auth-confirm-identity"><h1>{t('pages.confirmIdentity')}</h1>{token ? <><p>{hasPendingAction ? t('messages.pendingSettingsAction') : t('messages.confirmedIdentity')}</p><Button onClick={() => navigate('/settings/account')}>{t('actions.continue')}</Button></> : <p role="alert">{t('messages.invalidLink')}</p>}</main> }
