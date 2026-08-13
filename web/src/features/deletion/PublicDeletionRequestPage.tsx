import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { FormField, formShellClasses } from '../../components/ui/formShell'
import { Input } from '../../components/ui/input'
import { PageHeader } from '../../components/ui/pageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { Section } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'

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
    <main data-testid="screen-account-deletion">
      <PageScaffold width="form">
        <PageHeader
          subtitle={t('publicDeletion.requestDescription')}
          title={t('publicDeletion.requestTitle')}
        />
        {/* Not FormShell: it hardcodes `noValidate`, and this form's only guard
            against an empty or malformed address is the native constraint. */}
        <form className={formShellClasses} onSubmit={submit}>
          <FormField htmlFor="deletion-email" label={t('publicDeletion.email')}>
            <Input
              id="deletion-email"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              required
            />
          </FormField>
          <div className="flex">
            <Button className="ml-auto" type="submit" variant="primary">
              {t('publicDeletion.sendEmail')}
            </Button>
          </div>
        </form>
        {sent && (
          <StatusBanner role="status" variant="success">
            {t('publicDeletion.emailSent')}
          </StatusBanner>
        )}
        <Section
          className="gap-3"
          description={t('publicDeletion.steamDescription')}
        >
          <div className="flex">
            <Button
              variant="neutral"
              onClick={() => window.location.assign('/api/v1/account-deletion-requests/steam/start')}
            >
              {t('publicDeletion.proveWithSteam')}
            </Button>
          </div>
        </Section>
        {errorMessage && (
          <StatusBanner role="alert" variant="danger">
            {errorMessage}
          </StatusBanner>
        )}
      </PageScaffold>
    </main>
  )
}
