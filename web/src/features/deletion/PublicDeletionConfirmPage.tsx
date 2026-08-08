import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'

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
    <main
      className="mx-auto max-w-xl space-y-6 p-4 sm:p-8"
      data-testid="screen-account-deletion-confirm"
    >
      <h1 className="text-h1 font-display font-bold text-ink">
        {t('publicDeletion.confirmTitle')}
      </h1>
      {!token ? (
        <p role="alert">{t('publicDeletion.invalidLink')}</p>
      ) : confirmed ? (
        <p role="status">{t('publicDeletion.confirmed')}</p>
      ) : (
        <div className="space-y-3 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card">
          <p>{t('publicDeletion.confirmDescription')}</p>
          <Button variant="danger" onClick={() => void confirmDeletion()}>
            {t('publicDeletion.confirm')}
          </Button>
        </div>
      )}
      {errorMessage && <p role="alert">{errorMessage}</p>}
    </main>
  )
}
