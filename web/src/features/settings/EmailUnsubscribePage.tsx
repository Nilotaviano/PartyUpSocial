import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { PageHeader } from '../../components/shell/PageHeader'
import { PageScaffold } from '../../components/ui/pageScaffold'
import { SectionCard } from '../../components/ui/section'
import { StatusBanner } from '../../components/ui/statusBanner'

function readTokenFromHash() {
  const token = new URLSearchParams(window.location.hash.slice(1)).get('token')
  window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search)
  return token
}

export function EmailUnsubscribePage() {
  const { t } = useTranslation(['settings', 'errors'])
  const [token] = useState(readTokenFromHash)
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState<unknown>()
  const [submitting, setSubmitting] = useState(false)

  async function confirmUnsubscribe() {
    if (!token) {
      return
    }

    setError(undefined)
    setSubmitting(true)

    try {
      await client.POST('/api/v1/email-notifications/unsubscribe', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { token },
      })
      setConfirmed(true)
    } catch (caught) {
      setError(caught)
    } finally {
      setSubmitting(false)
    }
  }

  const errorMessage = error
    ? t(isApiError(error) ? error.code : 'generic', { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) })
    : null

  return (
    <main data-testid="screen-email-unsubscribe">
      <PageScaffold width="form">
        <PageHeader title={t('emailUnsubscribe.confirmTitle')} />
        {!token ? (
          <StatusBanner role="alert" variant="danger">
            {t('emailUnsubscribe.invalidLink')}
          </StatusBanner>
        ) : confirmed ? (
          <StatusBanner role="status" variant="success">
            {t('emailUnsubscribe.confirmed')}
          </StatusBanner>
        ) : (
          <SectionCard>
            <p>{t('emailUnsubscribe.confirmDescription')}</p>
            <div className="flex">
              <Button
                aria-busy={submitting}
                className="ml-auto"
                disabled={submitting}
                variant="danger"
                onClick={() => void confirmUnsubscribe()}
              >
                {t('emailUnsubscribe.confirm')}
              </Button>
            </div>
          </SectionCard>
        )}
        {errorMessage && (
          <StatusBanner role="alert" variant="danger">
            {errorMessage}
          </StatusBanner>
        )}
      </PageScaffold>
    </main>
  )
}
