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
    <main data-testid="screen-account-deletion-confirm">
      <h1>{t('publicDeletion.confirmTitle')}</h1>
      {!token ? (
        <p role="alert">{t('publicDeletion.invalidLink')}</p>
      ) : confirmed ? (
        <p role="status">{t('publicDeletion.confirmed')}</p>
      ) : (
        <>
          <p>{t('publicDeletion.confirmDescription')}</p>
          <Button onClick={() => void confirmDeletion()}>{t('publicDeletion.confirm')}</Button>
        </>
      )}
      {errorMessage && <p role="alert">{errorMessage}</p>}
    </main>
  )
}
