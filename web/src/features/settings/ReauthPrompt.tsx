import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { consumeReauthProof } from '../../auth/reauthProof'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import { setPendingSettingsAction, type ReauthAction } from './reauthAction'

export type { ReauthAction } from './reauthAction'

type ReauthProof = {
  currentPassword?: string
  reauthToken?: string
}

type ReauthPromptProps = {
  action: ReauthAction | null
  methods: { kind: string; linked: boolean }[]
  onOpenChange: (open: boolean) => void
  onConfirmed: (proof: ReauthProof) => Promise<void>
  returnFocusTo?: HTMLElement | null
}

const primaryActionClassName =
  'min-h-11 rounded-md bg-indigo-700 px-4 py-2 font-medium text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-700 disabled:cursor-not-allowed disabled:opacity-60'
const secondaryActionClassName =
  'min-h-11 rounded-md border border-slate-300 px-4 py-2 font-medium text-slate-900 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-60'

function hasMethod(methods: ReauthPromptProps['methods'], kind: string) {
  return methods.some(
    (method) => method.linked && method.kind.toLowerCase() === kind,
  )
}

export function ReauthPrompt({
  action,
  methods,
  onOpenChange,
  onConfirmed,
  returnFocusTo,
}: ReauthPromptProps) {
  const { t } = useTranslation(['settings', 'errors'])
  const [password, setPassword] = useState('')
  const [error, setError] = useState<unknown>()
  const [submitting, setSubmitting] = useState(false)
  const [proof, setProof] = useState<string | null>(null)
  const open = action !== null
  const passwordAvailable = hasMethod(methods, 'password')
  const externalProviders = methods
    .filter(
      (method) =>
        method.linked &&
        ['google', 'steam'].includes(method.kind.toLowerCase()),
    )
    .map((method) => method.kind.toLowerCase())
  const emailAvailable =
    hasMethod(methods, 'magiclink') || hasMethod(methods, 'magic-link')

  useEffect(() => {
    if (open) {
      queueMicrotask(() => {
        setProof(consumeReauthProof())
        setError(undefined)
      })
    }
  }, [open])

  async function submitPassword() {
    setSubmitting(true)
    setError(undefined)
    try {
      await onConfirmed({ currentPassword: password })
      onOpenChange(false)
    } catch (caught) {
      setError(caught)
    } finally {
      setSubmitting(false)
    }
  }

  async function continueWithProof() {
    if (!proof) return
    setSubmitting(true)
    setError(undefined)
    try {
      await onConfirmed({ reauthToken: proof })
      setProof(null)
      onOpenChange(false)
    } catch (caught) {
      setError(caught)
    } finally {
      setSubmitting(false)
    }
  }

  async function requestEmailProof() {
    if (!action) return
    setSubmitting(true)
    setError(undefined)
    setPendingSettingsAction(action)
    try {
      await client.POST('/api/v1/auth/reauth/email', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
      })
    } catch (caught) {
      setError(caught)
    } finally {
      setSubmitting(false)
    }
  }

  async function requestExternalProof(provider: string) {
    if (!action) return
    setSubmitting(true)
    setError(undefined)
    setPendingSettingsAction(action)
    try {
      const { data } = await client.POST('/api/v1/auth/external/intent', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: {
          provider,
          mode: 'reauth',
          returnTo: '/settings/account',
          ageGateAttested: null,
          currentPassword: null,
          reauthToken: null,
        },
      })
      if (!data) throw new Error('External intent was empty.')
      window.location.assign(
        `/api/v1/auth/external/${provider}/start?intent=${encodeURIComponent(data.intent)}`,
      )
    } catch (caught) {
      setError(caught)
      setSubmitting(false)
    }
  }

  const errorCode =
    isApiError(error) && error.code === 'last_auth_method'
      ? 'auth.lastMethod'
      : isApiError(error) && error.code === 'reauth_required'
        ? 'auth.reauthRequired'
        : error
          ? 'generic'
          : null
  const errorMessage = errorCode
    ? t(errorCode, {
        ns: 'errors',
        defaultValue: t('generic', { ns: 'errors' }),
      })
    : null

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-40 bg-slate-950/40"
          data-testid="reauth-overlay"
        />
        <Dialog.Content
          aria-describedby="reauth-description"
          className="fixed left-1/2 top-1/2 z-50 max-h-[calc(100dvh_-_2rem)] w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-xl bg-white p-6 shadow-xl focus:outline-none"
          onCloseAutoFocus={(event) => {
            if (!returnFocusTo?.isConnected) return
            event.preventDefault()
            returnFocusTo.focus()
          }}
        >
          <Dialog.Title className="text-lg font-semibold text-slate-950">
            {t('reauth.title')}
          </Dialog.Title>
          <Dialog.Description
            className="mt-2 text-sm text-slate-700"
            id="reauth-description"
          >
            {t('reauth.description')}
          </Dialog.Description>
          {action?.kind === 'deleteAccount' && (
            <div className="mt-4 space-y-2 text-sm text-slate-700">
              <p>{t('delete.grace')}</p>
              <p>{t('delete.loginCancels')}</p>
            </div>
          )}
          {proof ? (
            <Button
              className={primaryActionClassName + ' mt-6 w-full'}
              disabled={submitting}
              onClick={() => void continueWithProof()}
            >
              {t('reauth.confirmIdentity')}
            </Button>
          ) : (
            <div className="mt-6 grid gap-3">
              {passwordAvailable && (
                <div className="grid gap-3 rounded-lg border border-slate-200 p-4">
                  <Label
                    className="text-sm font-medium text-slate-800"
                    htmlFor="reauth-password"
                  >
                    {t('reauth.password')}
                  </Label>
                  <Input
                    className="min-h-11 w-full rounded-md border border-slate-300 px-3 py-2 text-slate-950 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                    id="reauth-password"
                    type="password"
                    value={password}
                    onChange={(event) => setPassword(event.target.value)}
                  />
                  <Button
                    className={primaryActionClassName}
                    disabled={submitting || password.length === 0}
                    onClick={() => void submitPassword()}
                  >
                    {t('reauth.confirmWithPassword')}
                  </Button>
                </div>
              )}
              {externalProviders.map((provider) => (
                <Button
                  key={provider}
                  className={secondaryActionClassName}
                  disabled={submitting}
                  onClick={() => void requestExternalProof(provider)}
                >
                  {t('reauth.confirmWithProvider', {
                    provider: provider === 'steam' ? 'Steam' : 'Google',
                  })}
                </Button>
              ))}
              {emailAvailable && (
                <Button
                  className={secondaryActionClassName}
                  disabled={submitting}
                  onClick={() => void requestEmailProof()}
                >
                  {t('reauth.emailLink')}
                </Button>
              )}
            </div>
          )}
          {errorMessage && (
            <p className="mt-4 text-sm text-red-700" role="alert">
              {errorMessage}
            </p>
          )}
          <div className="mt-6 flex justify-end">
            <Dialog.Close asChild>
              <Button
                className={secondaryActionClassName}
                disabled={submitting}
              >
                {t('actions.cancel')}
              </Button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
