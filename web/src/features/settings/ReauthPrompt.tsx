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
          className="fixed inset-0 z-40 bg-scrim"
          data-testid="reauth-overlay"
        />
        {/* `max-h-[calc(100dvh_-_2rem)]` keeps this scrolling dialog inside the
            visual viewport on a short mobile screen; no `max-h-*` utility is
            dvh-relative. */}
        <Dialog.Content
          aria-describedby="reauth-description"
          className="fixed left-1/2 top-1/2 z-50 max-h-[calc(100dvh_-_2rem)] w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            if (!returnFocusTo?.isConnected) return
            event.preventDefault()
            returnFocusTo.focus()
          }}
        >
          <Dialog.Title className="font-display text-h2 font-semibold text-ink">
            {t('reauth.title')}
          </Dialog.Title>
          <Dialog.Description
            className="mt-2 text-ink-soft"
            id="reauth-description"
          >
            {t('reauth.description')}
          </Dialog.Description>
          {action?.kind === 'deleteAccount' && (
            <div className="mt-4 space-y-2 text-ink-soft">
              <p>{t('delete.grace')}</p>
              <p>{t('delete.loginCancels')}</p>
            </div>
          )}
          {proof ? (
            <Button
              className="mt-6 w-full"
              disabled={submitting}
              variant="primary"
              onClick={() => void continueWithProof()}
            >
              {t('reauth.confirmIdentity')}
            </Button>
          ) : (
            <div className="mt-6 grid gap-3">
              {passwordAvailable && (
                <div className="grid gap-3 rounded-card border-2 border-ink p-4">
                  <Label htmlFor="reauth-password">
                    {t('reauth.password')}
                  </Label>
                  <Input
                    id="reauth-password"
                    type="password"
                    value={password}
                    onChange={(event) => setPassword(event.target.value)}
                  />
                  <Button
                    disabled={submitting || password.length === 0}
                    variant="primary"
                    onClick={() => void submitPassword()}
                  >
                    {t('reauth.confirmWithPassword')}
                  </Button>
                </div>
              )}
              {externalProviders.map((provider) => (
                <Button
                  key={provider}
                  disabled={submitting}
                  variant="neutral"
                  onClick={() => void requestExternalProof(provider)}
                >
                  {t('reauth.confirmWithProvider', {
                    provider: provider === 'steam' ? 'Steam' : 'Google',
                  })}
                </Button>
              ))}
              {emailAvailable && (
                <Button
                  disabled={submitting}
                  variant="neutral"
                  onClick={() => void requestEmailProof()}
                >
                  {t('reauth.emailLink')}
                </Button>
              )}
            </div>
          )}
          {errorMessage && (
            <p className="mt-4 text-danger" role="alert">
              {errorMessage}
            </p>
          )}
          <div className="mt-6 flex justify-end">
            <Dialog.Close asChild>
              <Button disabled={submitting} variant="neutral">
                {t('actions.cancel')}
              </Button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
