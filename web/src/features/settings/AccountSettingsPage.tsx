import { useEffect, useState, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router'

import { ApiError, client, isApiError } from '../../api/client'
import { useSession, sessionQueryKey } from '../../auth/useSession'
import { startExternalProviderWithRetry } from '../../auth/externalIntent'
import { Button } from '../../components/ui/button'
import { FormField } from '../../components/ui/formShell'
import { Input } from '../../components/ui/input'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section, SectionCard } from '../../components/ui/section'
import { Select } from '../../components/ui/select'
import { StatusBanner } from '../../components/ui/statusBanner'
import { TimeZoneField, isKnownTimeZone } from '../../components/TimeZoneField'
import { ReauthPrompt, type ReauthAction } from './ReauthPrompt'
import { clearPendingSettingsAction, getPendingSettingsAction } from './reauthAction'
import { PASSWORD_MIN_LENGTH } from '../../auth/passwordPolicy'
import { SettingsNavigation } from './BlockedUsersPage'
import { ThemeToggle } from './ThemeToggle'

const methodsQueryKey = ['auth-methods'] as const

async function getAuthMethods() {
  const { data } = await client.GET('/api/v1/auth/methods')
  if (!data) throw new Error('Authentication methods response was empty.')
  return data.methods
}

function errorKey(error: unknown) {
  if (!isApiError(error)) return 'generic'
  switch (error.code) {
    case 'last_auth_method':
      return 'auth.lastMethod'
    case 'method_not_removable':
      return 'auth.methodNotRemovable'
    case 'reauth_required':
      return 'auth.reauthRequired'
    case 'email_required':
      return 'auth.emailRequired'
    default:
      return error.code
  }
}

type PasswordProof = { currentPassword?: string; reauthToken?: string }

export function AccountSettingsPage() {
  const { t, i18n } = useTranslation(['settings', 'errors', 'tour'])
  const location = useLocation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: session } = useSession()
  const { data: methods = [], isPending } = useQuery({
    queryKey: methodsQueryKey,
    queryFn: getAuthMethods,
  })
  const [locale, setLocale] = useState(i18n.language)
  const [action, setAction] = useState<ReauthAction | null>(null)
  const [error, setError] = useState<unknown>()
  const [saved, setSaved] = useState(false)
  const [graceEndsAt, setGraceEndsAt] = useState<string | null>(null)
  const [returnFocusTo, setReturnFocusTo] = useState<HTMLElement | null>(null)
  const [currentPassword, setCurrentPassword] = useState('')
  const [changeCurrentMissing, setChangeCurrentMissing] = useState(false)
  const [changeNewPassword, setChangeNewPassword] = useState('')
  const [changeTooShort, setChangeTooShort] = useState(false)
  const [changeWrongCurrent, setChangeWrongCurrent] = useState(false)
  const [changeError, setChangeError] = useState<unknown>()
  const [changeSuccess, setChangeSuccess] = useState(false)
  const [setPasswordValue, setSetPasswordValue] = useState('')
  const [setPasswordTooShort, setSetPasswordTooShort] = useState(false)
  const [setPasswordError, setSetPasswordError] = useState<unknown>()
  const [setPasswordSuccess, setSetPasswordSuccess] = useState(false)
  const [setPasswordProof, setSetPasswordProof] = useState<PasswordProof | null>(null)
  const [timeZoneInvalid, setTimeZoneInvalid] = useState(false)

  useEffect(() => {
    const accountLocale = session?.profile?.locale
    if (accountLocale) {
      queueMicrotask(() => setLocale(accountLocale))
      void i18n.changeLanguage(accountLocale)
    }
  }, [i18n, session?.profile?.locale])

  useEffect(() => {
    const pendingAction = getPendingSettingsAction()
    if (pendingAction) queueMicrotask(() => setAction(pendingAction))
  }, [])

  async function saveLocale(value: string) {
    setError(undefined)
    setSaved(false)
    try {
      await client.PUT('/api/v1/me/locale', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { locale: value },
      })
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
    const timeZone = String(new FormData(event.currentTarget).get('timeZone') ?? '')
    if (!isKnownTimeZone(timeZone, session?.profile?.timeZone)) {
      setTimeZoneInvalid(true)
      return
    }
    setTimeZoneInvalid(false)
    try {
      await client.PUT('/api/v1/me/time-zone', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { timeZone },
      })
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    } catch (caught) {
      setError(caught)
    }
  }
  async function confirmAction(proof: { currentPassword?: string; reauthToken?: string }) {
    setError(undefined)
    if (action?.kind === 'deleteAccount') {
      const result = (await client.POST('/api/v1/me/deletion', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          currentPassword: proof.currentPassword ?? null,
          reauthToken: proof.reauthToken ?? null,
        },
      } as never)) as { data?: { graceEndsAt?: string } }
      setGraceEndsAt(result.data?.graceEndsAt ?? null)
      clearPendingSettingsAction()
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      return
    }
    if (action?.kind === 'unlinkAuthMethod') {
      await client.DELETE('/api/v1/auth/methods/{kind}', {
        params: {
          header: { 'X-CSRF-TOKEN': '' },
          path: { kind: action.method },
        },
        body: {
          currentPassword: proof.currentPassword ?? null,
          reauthToken: proof.reauthToken ?? null,
        },
      } as never)
      clearPendingSettingsAction()
      await queryClient.invalidateQueries({ queryKey: methodsQueryKey })
      return
    }
    if (action?.kind === 'linkAuthMethod') {
      await linkProvider(action.provider, proof)
    }
    if (action?.kind === 'setPassword') {
      setSetPasswordProof({ currentPassword: proof.currentPassword, reauthToken: proof.reauthToken })
      clearPendingSettingsAction()
    }
  }

  async function submitChangePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setChangeCurrentMissing(false)
    setChangeTooShort(false)
    setChangeWrongCurrent(false)
    setChangeError(undefined)
    setChangeSuccess(false)
    if (!currentPassword) {
      setChangeCurrentMissing(true)
      return
    }
    if (changeNewPassword.length < PASSWORD_MIN_LENGTH) {
      setChangeTooShort(true)
      return
    }
    try {
      await client.POST('/api/v1/auth/password/change', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { currentPassword, newPassword: changeNewPassword },
      })
      setCurrentPassword('')
      setChangeNewPassword('')
      setChangeSuccess(true)
      await queryClient.invalidateQueries({ queryKey: methodsQueryKey })
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    } catch (caught) {
      if (isApiError(caught) && caught.code === 'auth_validation') {
        setChangeTooShort(true)
      } else if (isApiError(caught) && caught.code === 'reauth_required') {
        setChangeWrongCurrent(true)
      } else {
        setChangeError(caught)
      }
    }
  }

  async function submitSetPassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!setPasswordProof) return
    setSetPasswordTooShort(false)
    setSetPasswordError(undefined)
    setSetPasswordSuccess(false)
    if (setPasswordValue.length < PASSWORD_MIN_LENGTH) {
      setSetPasswordTooShort(true)
      return
    }
    try {
      await client.POST('/api/v1/auth/password/set', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          newPassword: setPasswordValue,
          currentPassword: setPasswordProof.currentPassword ?? null,
          reauthToken: setPasswordProof.reauthToken ?? null,
        },
      })
      setSetPasswordValue('')
      setSetPasswordProof(null)
      setSetPasswordSuccess(true)
      await queryClient.invalidateQueries({ queryKey: methodsQueryKey })
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    } catch (caught) {
      if (isApiError(caught) && caught.code === 'auth_validation') {
        setSetPasswordTooShort(true)
      } else {
        setSetPasswordError(caught)
      }
    }
  }

  async function linkProvider(provider: string, proof: { currentPassword?: string; reauthToken?: string }) {
    setError(undefined)
    await startExternalProviderWithRetry(
      async () => {
        const { data } = await client.POST('/api/v1/auth/external/intent', {
          params: { header: { 'X-CSRF-TOKEN': '' } },
          body: {
            provider,
            mode: 'link',
            returnTo: '/settings/account',
            ageGateAttested: null,
            currentPassword: proof.currentPassword ?? null,
            reauthToken: proof.reauthToken ?? null,
          },
        })
        if (!data) throw new ApiError(500, 'request_failed')
        return data
      },
      async (intent) => {
        window.location.assign(`/api/v1/auth/external/${provider}/start?intent=${encodeURIComponent(intent)}`)
      },
    )
  }

  // /auth/methods emits every method kind unconditionally; only the linked flag says whether a
  // password hash actually exists (same linked-aware idiom as the unlinked-provider filter below).
  const hasPassword = methods.some((method) => method.linked && method.kind.toLowerCase() === 'password')
  const emailMethod = methods.find((method) => method.kind.toLowerCase().includes('magic'))
  const configuredProviders = (session?.providers ?? []).filter((provider) => ['google', 'steam'].includes(provider.toLowerCase()))
  const unlinkedProviders = configuredProviders.filter((provider) => !methods.some((method) => method.linked && method.kind.toLowerCase() === provider.toLowerCase()))
  const errorMessage = error
    ? t(errorKey(error), {
        ns: 'errors',
        defaultValue: t('generic', { ns: 'errors' }),
      })
    : null
  const changeErrorMessage = changeError
    ? t(errorKey(changeError), {
        ns: 'errors',
        defaultValue: t('generic', { ns: 'errors' }),
      })
    : null
  const setPasswordErrorMessage = setPasswordError
    ? t(errorKey(setPasswordError), {
        ns: 'errors',
        defaultValue: t('generic', { ns: 'errors' }),
      })
    : null

  function authMethodName(kind: string) {
    return t(`authMethods.kind.${kind.toLowerCase()}`, { defaultValue: kind })
  }

  return (
    <main data-testid="screen-settings-account">
      <PageScaffold>
      <PageHeader title={t('pages.account')} />
      <SettingsNavigation />
      {(location.state as { identityConfirmed?: boolean } | null)?.identityConfirmed && <p className="text-ink" role="status">{t('reauth.confirmedIdentityChooseAction')}</p>}
      <Section title={t('email.title')} titleId="account-email-heading">
        <p className="text-ink-soft">{emailMethod?.detail ?? t('email.unavailable')}</p>
      </Section>
      <Section
        description={hasPassword ? t('password.enabled') : t('password.notSet')}
        title={t('password.title')}
        titleId="account-password-heading"
      >
        {hasPassword ? (
          <form className="flex max-w-form flex-col gap-4" noValidate onSubmit={(event) => void submitChangePassword(event)}>
            <FormField htmlFor="password-current" label={t('password.currentLabel')}>
              <Input
                id="password-current"
                type="password"
                value={currentPassword}
                onChange={(event) => setCurrentPassword(event.target.value)}
                aria-invalid={changeCurrentMissing || changeWrongCurrent || undefined}
                aria-describedby={
                  changeCurrentMissing
                    ? 'password-current-error'
                    : changeWrongCurrent
                      ? 'password-current-incorrect'
                      : undefined
                }
                required
              />
            </FormField>
            {changeCurrentMissing && <p id="password-current-error" role="alert">{t('password.currentRequired')}</p>}
            <FormField htmlFor="password-change-new" label={t('password.newLabel')}>
              <Input
                id="password-change-new"
                type="password"
                value={changeNewPassword}
                onChange={(event) => setChangeNewPassword(event.target.value)}
                minLength={PASSWORD_MIN_LENGTH}
                aria-describedby={changeTooShort ? 'password-change-new-hint password-change-new-error' : 'password-change-new-hint'}
                aria-invalid={changeTooShort || undefined}
                required
              />
            </FormField>
            <p className="text-body-sm text-ink-soft" id="password-change-new-hint">{t('password.hint')}</p>
            <Button className="self-start" type="submit" variant="primary">{t('password.changeSubmit')}</Button>
            {changeTooShort && <p id="password-change-new-error" role="alert">{t('password.tooShort')}</p>}
            {changeWrongCurrent && <p id="password-current-incorrect" role="alert">{t('password.currentIncorrect')}</p>}
            {changeErrorMessage && <p role="alert">{changeErrorMessage}</p>}
            {changeSuccess && <p className="text-ink" role="status">{t('password.changeSuccess')}</p>}
          </form>
        ) : setPasswordProof ? (
          <form className="flex max-w-form flex-col gap-4" noValidate onSubmit={(event) => void submitSetPassword(event)}>
            <FormField htmlFor="password-set-new" label={t('password.newLabel')}>
              <Input
                id="password-set-new"
                type="password"
                value={setPasswordValue}
                onChange={(event) => setSetPasswordValue(event.target.value)}
                minLength={PASSWORD_MIN_LENGTH}
                aria-describedby={setPasswordTooShort ? 'password-set-new-hint password-set-new-error' : 'password-set-new-hint'}
                aria-invalid={setPasswordTooShort || undefined}
                required
              />
            </FormField>
            <p className="text-body-sm text-ink-soft" id="password-set-new-hint">{t('password.hint')}</p>
            <Button className="self-start" type="submit" variant="primary">{t('password.setSubmit')}</Button>
            {setPasswordTooShort && <p id="password-set-new-error" role="alert">{t('password.tooShort')}</p>}
            {setPasswordErrorMessage && <p role="alert">{setPasswordErrorMessage}</p>}
          </form>
        ) : (
          <Button
            className="self-start"
            variant="neutral"
            onClick={(event) => {
              setReturnFocusTo(event.currentTarget)
              setAction({ kind: 'setPassword' })
            }}
          >
            {t('password.setStart')}
          </Button>
        )}
        {setPasswordSuccess && <p className="text-ink" role="status">{t('password.setSuccess')}</p>}
      </Section>
      <Section title={t('authMethods.title')} titleId="auth-methods-heading">
        {isPending ? (
          <p className="text-ink-soft">{t('loading')}</p>
        ) : (
          <ul>
            {methods.map((method) => (
              <li className="flex flex-wrap items-center justify-between gap-2 border-t border-ink/25 px-2 py-3 first:border-t-0" key={method.kind}>
                <span className="text-ink">{method.detail ?? authMethodName(method.kind)}</span>
                {method.removable && (
                  <Button
                    variant="danger"
                    onClick={(event) => {
                      setReturnFocusTo(event.currentTarget)
                      setAction({
                        kind: 'unlinkAuthMethod',
                        method: method.kind,
                      })
                    }}
                  >
                    {t('authMethods.unlink', { method: authMethodName(method.kind) })}
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
        {unlinkedProviders.length > 0 && (
          <div className="flex flex-col gap-2">
            <h3 className="font-display text-h3 font-semibold text-ink">{t('authMethods.linkTitle')}</h3>
            <div className="flex flex-wrap gap-2">
              {unlinkedProviders.map((provider) => (
                <Button
                  key={provider}
                  variant="neutral"
                  onClick={(event) => {
                    setReturnFocusTo(event.currentTarget)
                    setAction({ kind: 'linkAuthMethod', provider })
                  }}
                >
                  {t('authMethods.link', {
                    provider: provider === 'google' ? 'Google' : 'Steam',
                  })}
                </Button>
              ))}
            </div>
          </div>
        )}
      </Section>
      <Section title={t('language.title')} titleId="language-heading">
        <div className="flex max-w-form flex-col gap-1.5">
          <label className="font-body font-semibold text-ink" htmlFor="locale">{t('language.label')}</label>
          <Select.Root value={locale} onValueChange={(value) => void saveLocale(value)}>
            <Select.Trigger id="locale" aria-label={t('language.label')}>
              <Select.Value />
            </Select.Trigger>
            <Select.Portal>
              <Select.Content>
                <Select.Viewport>
                  <Select.Item value="en">
                    <Select.ItemText>{t('language.en')}</Select.ItemText>
                  </Select.Item>
                  <Select.Item value="pt-BR">
                    <Select.ItemText>{t('language.ptBR')}</Select.ItemText>
                  </Select.Item>
                  <Select.Item value="es">
                    <Select.ItemText>{t('language.es')}</Select.ItemText>
                  </Select.Item>
                </Select.Viewport>
              </Select.Content>
            </Select.Portal>
          </Select.Root>
          {saved && <p className="text-ink" role="status">{t('language.saved')}</p>}
        </div>
      </Section>
      <Section title={t('theme.title')} titleId="theme-heading">
        <ThemeToggle />
      </Section>
      <Section
        description={t('replay.body', { ns: 'tour' })}
        title={t('replay.title', { ns: 'tour' })}
        titleId="tour-heading"
      >
        <Button
          className="self-start"
          variant="neutral"
          onClick={() => navigate('/app', { state: { replayTour: true } })}
        >
          {t('replay.action', { ns: 'tour' })}
        </Button>
      </Section>
      <Section title={t('fields.timeZone', { ns: 'profile' })} titleId="time-zone-heading">
        <form className="flex max-w-form flex-col gap-4" noValidate onSubmit={(event) => void saveTimeZone(event)}>
          <TimeZoneField
            defaultValue={session?.profile?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone}
            errorMessage={timeZoneInvalid ? t('timeZoneField.invalid', { ns: 'profile' }) : undefined}
            hint={t('timeZoneField.hint', { ns: 'profile' })}
            id="account-time-zone"
            label={t('fields.timeZone', { ns: 'profile' })}
            name="timeZone"
            storedZone={session?.profile?.timeZone}
          />
          <Button className="self-start" type="submit" variant="primary">{t('actions.saveTimeZone', { ns: 'profile' })}</Button>
        </form>
      </Section>
      {/* Hazard boundary (§3.1): the one SectionCard on this page. */}
      <SectionCard title={t('delete.title')} titleId="delete-account-heading">
        <div className="flex flex-col gap-2">
          <p className="text-ink-soft">{t('delete.grace')}</p>
          <p className="text-ink-soft">{t('delete.loginCancels')}</p>
        </div>
        <Button
          className="self-start"
          variant="danger"
          onClick={(event) => {
            setReturnFocusTo(event.currentTarget)
            setAction({ kind: 'deleteAccount' })
          }}
        >
          {t('delete.start')}
        </Button>
        {graceEndsAt && <p className="text-ink" role="status">{t('delete.scheduled', { graceEndsAt })}</p>}
      </SectionCard>
      {errorMessage && <StatusBanner role="alert" variant="danger">{errorMessage}</StatusBanner>}
      <ReauthPrompt
        action={action}
        methods={methods}
        onOpenChange={(open) => {
          if (!open) {
            clearPendingSettingsAction()
            setAction(null)
          }
        }}
        onConfirmed={confirmAction}
        returnFocusTo={returnFocusTo}
      />
      </PageScaffold>
    </main>
  )
}
