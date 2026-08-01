import { useEffect, useState, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router'
import { client, isApiError } from '../../api/client'
import { useSession, sessionQueryKey } from '../../auth/useSession'
import { Button } from '../../components/ui/button'
import { Label } from '../../components/ui/label'
import { Input } from '../../components/ui/input'
import { Select } from '../../components/ui/select'
import { ReauthPrompt, type ReauthAction } from './ReauthPrompt'

const methodsQueryKey = ['auth-methods'] as const

type PendingLocationState = { pendingReauthAction?: ReauthAction }

async function getAuthMethods() {
  const { data } = await client.GET('/api/v1/auth/methods')
  if (!data) throw new Error('Authentication methods response was empty.')
  return data.methods
}

function errorKey(error: unknown) {
  if (!isApiError(error)) return 'generic'
  switch (error.code) {
    case 'last_auth_method': return 'auth.lastMethod'
    case 'method_not_removable': return 'auth.methodNotRemovable'
    case 'reauth_required': return 'auth.reauthRequired'
    default: return error.code
  }
}

export function AccountSettingsPage() {
  const { t, i18n } = useTranslation(['settings', 'errors'])
  const location = useLocation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: session } = useSession()
  const { data: methods = [], isPending } = useQuery({ queryKey: methodsQueryKey, queryFn: getAuthMethods })
  const [locale, setLocale] = useState(i18n.language)
  const [action, setAction] = useState<ReauthAction | null>((location.state as PendingLocationState | null)?.pendingReauthAction ?? null)
  const [error, setError] = useState<unknown>()
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    const accountLocale = session?.profile?.locale
    if (accountLocale) {
      queueMicrotask(() => setLocale(accountLocale))
      void i18n.changeLanguage(accountLocale)
    }
  }, [i18n, session?.profile?.locale])

  useEffect(() => {
    const pendingAction = (location.state as PendingLocationState | null)?.pendingReauthAction
    if (pendingAction) {
      queueMicrotask(() => setAction(pendingAction))
      navigate(location.pathname, { replace: true, state: null })
    }
  }, [location.pathname, location.state, navigate])

  async function saveLocale(value: string) {
    setError(undefined)
    setSaved(false)
    try {
      await client.PUT('/api/v1/me/locale', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { locale: value } })
      setLocale(value)
      await i18n.changeLanguage(value)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      setSaved(true)
    } catch (caught) {
      setError(caught)
    }
  }

  async function saveTimeZone(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError(undefined)
    try {
      const timeZone = String(new FormData(event.currentTarget).get('timeZone') ?? '')
      await client.PUT('/api/v1/me/time-zone', { params: { header: { 'X-CSRF-TOKEN': '' } }, body: { timeZone } })
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    } catch (caught) {
      setError(caught)
    }
  }
  async function confirmAction(proof: { currentPassword?: string; reauthToken?: string }) {
    setError(undefined)
    if (action === 'delete-account') {
      await client.POST('/api/v1/me/deletion', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { currentPassword: proof.currentPassword ?? null, reauthToken: proof.reauthToken ?? null },
      } as never)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      navigate('/welcome', { replace: true })
      return
    }
    if (action?.startsWith('unlink:')) {
      const kind = action.slice('unlink:'.length)
      await client.DELETE('/api/v1/auth/methods/{kind}', {
        params: { header: { 'X-CSRF-TOKEN': '' }, path: { kind } },
        body: { currentPassword: proof.currentPassword ?? null, reauthToken: proof.reauthToken ?? null },
      } as never)
      await queryClient.invalidateQueries({ queryKey: methodsQueryKey })
    }
  }

  const emailMethod = methods.find((method) => method.kind.toLowerCase().includes('magic'))
  const errorMessage = error ? t(errorKey(error), { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) }) : null

  return (
    <main data-testid="screen-settings-account">
      <h1>{t('pages.account')}</h1>
      <section aria-labelledby="account-email-heading">
        <h2 id="account-email-heading">{t('email.title')}</h2>
        <p>{emailMethod?.detail ?? t('email.unavailable')}</p>
      </section>
      <section aria-labelledby="account-password-heading">
        <h2 id="account-password-heading">{t('password.title')}</h2>
        <p>{methods.some((method) => method.kind.toLowerCase() === 'password') ? t('password.enabled') : t('password.notSet')}</p>
      </section>
      <section aria-labelledby="auth-methods-heading">
        <h2 id="auth-methods-heading">{t('authMethods.title')}</h2>
        {isPending ? <p>{t('loading')}</p> : <ul>{methods.map((method) => (
          <li key={method.kind}>
            <span>{method.detail ?? method.kind}</span>
            {method.removable && <Button onClick={() => setAction(`unlink:${method.kind}`)}>{t('authMethods.unlink', { method: method.kind })}</Button>}
          </li>
        ))}</ul>}
      </section>
      <section aria-labelledby="language-heading">
        <h2 id="language-heading">{t('language.title')}</h2>
        <Label htmlFor="locale">{t('language.label')}</Label>
        <Select.Root value={locale} onValueChange={(value) => void saveLocale(value)}>
          <Select.Trigger id="locale" aria-label={t('language.label')}><Select.Value /></Select.Trigger>
          <Select.Portal><Select.Content><Select.Viewport>
            <Select.Item value="en"><Select.ItemText>{t('language.en')}</Select.ItemText></Select.Item>
            <Select.Item value="pt-BR"><Select.ItemText>{t('language.ptBR')}</Select.ItemText></Select.Item>
            <Select.Item value="es"><Select.ItemText>{t('language.es')}</Select.ItemText></Select.Item>
          </Select.Viewport></Select.Content></Select.Portal>
        </Select.Root>
        {saved && <p role="status">{t('language.saved')}</p>}
      </section>
      <section aria-labelledby="time-zone-heading">
        <h2 id="time-zone-heading">{t('fields.timeZone', { ns: 'profile' })}</h2>
        <form onSubmit={(event) => void saveTimeZone(event)}>
          <Label htmlFor="account-time-zone">{t('fields.timeZone', { ns: 'profile' })}</Label>
          <Input id="account-time-zone" name="timeZone" defaultValue={session?.profile?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone} required />
          <Button type="submit">{t('actions.saveTimeZone', { ns: 'profile' })}</Button>
        </form>
      </section>      <section aria-labelledby="delete-account-heading">
        <h2 id="delete-account-heading">{t('delete.title')}</h2>
        <p>{t('delete.grace')}</p>
        <p>{t('delete.loginCancels')}</p>
        <Button onClick={() => setAction('delete-account')}>{t('delete.start')}</Button>
      </section>
      {errorMessage && <p role="alert">{errorMessage}</p>}
      <ReauthPrompt action={action} methods={methods} onOpenChange={(open) => { if (!open) setAction(null) }} onConfirmed={confirmAction} />
    </main>
  )
}