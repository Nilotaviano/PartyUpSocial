import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'

export function PublicDeletionRequestPage() {
  const { t } = useTranslation(['settings', 'errors'])
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<unknown>()

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError(undefined)

    try {
      await client.POST('/api/v1/account-deletion-requests', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { email },
      })
      setSent(true)
    } catch (caught) {
      setError(caught)
    }
  }

  const errorMessage = error
    ? t(isApiError(error) ? error.code : 'generic', { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) })
    : null

  return (
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-account-deletion"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('publicDeletion.requestTitle')}
      </h1>
      <p className="text-ink-soft">{t('publicDeletion.requestDescription')}</p>
      <form
        className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card"
        onSubmit={submit}
      >
        <div>
          <Label htmlFor="deletion-email">{t('publicDeletion.email')}</Label>
          <Input
            id="deletion-email"
            type="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            required
          />
        </div>
        <Button type="submit" variant="primary">
          {t('publicDeletion.sendEmail')}
        </Button>
      </form>
      {sent && <p role="status">{t('publicDeletion.emailSent')}</p>}
      <p className="text-ink-soft">{t('publicDeletion.steamDescription')}</p>
      <Button
        variant="primary"
        onClick={() => window.location.assign('/api/v1/account-deletion-requests/steam/start')}
      >
        {t('publicDeletion.proveWithSteam')}
      </Button>
      {errorMessage && <p role="alert">{errorMessage}</p>}
    </main>
  )
}
