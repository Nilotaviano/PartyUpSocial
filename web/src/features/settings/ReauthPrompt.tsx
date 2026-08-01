import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { consumeReauthProof } from '../../auth/reauthProof'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import { setPendingSettingsAction } from './reauthAction'

export type ReauthAction = 'delete-account' | `unlink:${string}`

type ReauthProof = {
  currentPassword?: string
  reauthToken?: string
}

type ReauthPromptProps = {
  action: ReauthAction | null
  methods: { kind: string; linked: boolean }[]
  onOpenChange: (open: boolean) => void
  onConfirmed: (proof: ReauthProof) => Promise<void>
}

function hasMethod(methods: ReauthPromptProps['methods'], kind: string) {
  return methods.some((method) => method.linked && method.kind.toLowerCase() === kind)
}

export function ReauthPrompt({ action, methods, onOpenChange, onConfirmed }: ReauthPromptProps) {
  const { t } = useTranslation(['settings', 'errors'])
  const [password, setPassword] = useState('')
  const [error, setError] = useState<unknown>()
  const [submitting, setSubmitting] = useState(false)
  const [proof, setProof] = useState<string | null>(null)
  const open = action !== null
  const passwordAvailable = hasMethod(methods, 'password')
  const externalProviders = methods
    .filter((method) => method.linked && ['google', 'steam'].includes(method.kind.toLowerCase()))
    .map((method) => method.kind.toLowerCase())
  const emailAvailable = hasMethod(methods, 'magiclink') || hasMethod(methods, 'magic-link')

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
      await client.POST('/api/v1/auth/reauth/email', { params: { header: { 'X-CSRF-TOKEN': '' } } })
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
        body: { provider, mode: 'reauth', returnTo: '/settings/account', ageGateAttested: null, currentPassword: null, reauthToken: null },
      })
      if (!data) throw new Error('External intent was empty.')
      window.location.assign(`/api/v1/auth/external/${provider}/start?intent=${encodeURIComponent(data.intent)}`)
    } catch (caught) {
      setError(caught)
      setSubmitting(false)
    }
  }

  const errorCode = isApiError(error) && error.code === 'last_auth_method' ? 'auth.lastMethod' : isApiError(error) && error.code === 'reauth_required' ? 'auth.reauthRequired' : error ? 'generic' : null
  const errorMessage = errorCode ? t(errorCode, { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) }) : null

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay />
        <Dialog.Content aria-describedby="reauth-description">
          <Dialog.Title>{t('reauth.title')}</Dialog.Title>
          <Dialog.Description id="reauth-description">{t('reauth.description')}</Dialog.Description>
          {action === 'delete-account' && <><p>{t('delete.grace')}</p><p>{t('delete.loginCancels')}</p></>}
          {proof ? (
            <Button disabled={submitting} onClick={() => void continueWithProof()}>{t('reauth.confirmIdentity')}</Button>
          ) : (
            <>
              {passwordAvailable && <div><Label htmlFor="reauth-password">{t('reauth.password')}</Label><Input id="reauth-password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} /><Button disabled={submitting || password.length === 0} onClick={() => void submitPassword()}>{t('reauth.confirmWithPassword')}</Button></div>}
              {externalProviders.map((provider) => <Button key={provider} disabled={submitting} onClick={() => void requestExternalProof(provider)}>{t('reauth.confirmWithProvider', { provider: provider === 'steam' ? 'Steam' : 'Google' })}</Button>)}
              {emailAvailable && <Button disabled={submitting} onClick={() => void requestEmailProof()}>{t('reauth.emailLink')}</Button>}
            </>
          )}
          {errorMessage && <p role="alert">{errorMessage}</p>}
          <Dialog.Close asChild><Button disabled={submitting}>{t('actions.cancel')}</Button></Dialog.Close>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}