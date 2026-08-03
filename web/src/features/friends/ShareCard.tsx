import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import QRCode from 'react-qr-code'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'

async function getFriendShare() {
  const { data } = await client.GET('/api/v1/friend-share')
  if (!data) throw new Error('Friend-share response was empty.')
  return data
}

export function ShareCard() {
  const { t } = useTranslation(['friends', 'errors'])
  const [announcement, setAnnouncement] = useState('')
  const share = useQuery({
    queryKey: ['friend-share'],
    queryFn: getFriendShare,
  })

  async function copy(value: string, label: string) {
    try {
      await navigator.clipboard.writeText(value)
      setAnnouncement(t('live.copied', { label }))
    } catch {
      setAnnouncement(t('generic', { ns: 'errors' }))
    }
  }

  return (
    <section
      aria-labelledby="friend-share-heading"
      className="rounded-2xl border border-indigo-200 bg-indigo-50 p-5 shadow-sm"
    >
      <h2
        id="friend-share-heading"
        className="text-xl font-semibold text-slate-950"
      >
        {t('sections.share')}
      </h2>
      {share.isPending && <p>{t('status.loading')}</p>}
      {share.error && (
        <p role="alert">
          {t(isApiError(share.error) ? share.error.code : 'generic', {
            ns: 'errors',
            defaultValue: t('generic', { ns: 'errors' }),
          })}
        </p>
      )}
      {share.data && (
        <div className="mt-4 grid gap-5 sm:grid-cols-[auto_1fr] sm:items-center">
          <div aria-hidden="true" className="w-fit rounded-xl bg-white p-3">
            <QRCode aria-hidden="true" size={152} value={share.data.addUrl} />
          </div>
          <div className="min-w-0 space-y-4">
            <p className="text-sm text-slate-600">{t('qr.description')}</p>
            <div>
              <p className="text-sm text-slate-600">{t('labels.friendCode')}</p>
              <p className="select-all font-mono text-2xl font-bold tracking-widest text-slate-950">
                {share.data.friendCode}
              </p>
              <Button
                onClick={() =>
                  void copy(share.data.friendCode, t('labels.friendCode'))
                }
              >
                {t('actions.copyCode')}
              </Button>
            </div>
            <div>
              <p className="text-sm text-slate-600">
                {t('share.addLinkDescription')}
              </p>
              <a
                className="block break-all text-indigo-700 underline"
                href={share.data.addUrl}
              >
                {share.data.addUrl}
              </a>
              <Button
                onClick={() =>
                  void copy(share.data.addUrl, t('actions.copyAddLink'))
                }
              >
                {t('actions.copyAddLink')}
              </Button>
            </div>
            <div>
              <p className="text-sm text-slate-600">
                {t('share.profileLinkDescription')}
              </p>
              <a
                className="block break-all text-indigo-700 underline"
                href={share.data.profileUrl}
              >
                {share.data.profileUrl}
              </a>
              <Button
                onClick={() =>
                  void copy(share.data.profileUrl, t('actions.copyProfileLink'))
                }
              >
                {t('actions.copyProfileLink')}
              </Button>
            </div>
          </div>
        </div>
      )}
      <p aria-live="polite" className="mt-3 text-sm text-emerald-700">
        {announcement}
      </p>
    </section>
  )
}
