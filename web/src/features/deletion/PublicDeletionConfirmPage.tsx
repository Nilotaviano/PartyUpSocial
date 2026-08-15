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

export function PublicDeletionConfirmPage() {
  const { t } = useTranslation(['settings', 'errors'])
  const [token] = useState(readTokenFromHash)
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState<unknown>()

  async function confirmDeletion() {
    if (!token) {
      return
    }

    setError(undefined)

    try {
      await client.POST('/api/v1/account-deletion-requests/confirm', {
        params: { header: { 'X-CSRF-TOKEN': '' } },
        body: { token },
      })
      setConfirmed(true)
    } catch (caught) {
      setError(caught)
    }
  }

  const errorMessage = error
    ? t(isApiError(error) ? error.code : 'generic', { ns: 'errors', defaultValue: t('generic', { ns: 'errors' }) })
    : null

  return (
    <main data-testid="screen-account-deletion-confirm">
      <PageScaffold width="form">
        <PageHeader title={t('publicDeletion.confirmTitle')} />
        {!token ? (
          <StatusBanner role="alert" variant="danger">
            {t('publicDeletion.invalidLink')}
          </StatusBanner>
        ) : confirmed ? (
          <StatusBanner role="status" variant="success">
            {t('publicDeletion.confirmed')}
          </StatusBanner>
        ) : (
          <SectionCard>
            <p>{t('publicDeletion.confirmDescription')}</p>
            <div className="flex">
              <Button
                className="ml-auto"
                variant="danger"
                onClick={() => void confirmDeletion()}
              >
                {t('publicDeletion.confirm')}
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
