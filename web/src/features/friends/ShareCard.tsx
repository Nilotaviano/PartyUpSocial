import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import QRCode from 'react-qr-code'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'

// PHASE5_1_DESIGN.md §1/§3: QR modules always render --color-qr-ink on a
// --color-qr chip in both themes (scannability; the pair is theme-invariant
// and never overridden under [data-theme=dark]) — hardcoded here since
// react-qr-code needs literal hex, not CSS var references.
const QR_CHIP_COLOR = '#FFFDF5' // --color-qr
const QR_MODULE_COLOR = '#1B2408' // --color-qr-ink

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
      className="rounded-card border-2 border-ink bg-card p-5 shadow-offset-card"
    >
      <h2
        id="friend-share-heading"
        className="font-display text-h2 font-semibold text-ink"
      >
        {t('sections.share')}
      </h2>
      {share.isPending && <p className="text-ink-soft">{t('status.loading')}</p>}
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
          <div aria-hidden="true" className="w-fit rounded-card bg-qr p-6">
            <QRCode
              aria-hidden="true"
              bgColor={QR_CHIP_COLOR}
              fgColor={QR_MODULE_COLOR}
              size={152}
              value={share.data.addUrl}
            />
          </div>
          <div className="min-w-0 space-y-4">
            <p className="text-ink-soft">{t('qr.description')}</p>
            <div>
              <p className="text-ink-soft">{t('labels.friendCode')}</p>
              <p className="select-all font-mono text-h1 font-bold tracking-widest text-ink">
                {share.data.friendCode}
              </p>
              <Button
                variant="neutral"
                onClick={() =>
                  void copy(share.data.friendCode, t('labels.friendCode'))
                }
              >
                {t('actions.copyCode')}
              </Button>
            </div>
            <div>
              <p className="text-ink-soft">
                {t('share.addLinkDescription')}
              </p>
              <a
                className="inline-flex min-h-11 items-center break-all text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                href={share.data.addUrl}
              >
                {share.data.addUrl}
              </a>
              <Button
                variant="neutral"
                onClick={() =>
                  void copy(share.data.addUrl, t('actions.copyAddLink'))
                }
              >
                {t('actions.copyAddLink')}
              </Button>
            </div>
            <div>
              <p className="text-ink-soft">
                {t('share.profileLinkDescription')}
              </p>
              <a
                className="inline-flex min-h-11 items-center break-all text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                href={share.data.profileUrl}
              >
                {share.data.profileUrl}
              </a>
              <Button
                variant="neutral"
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
      <p aria-live="polite" className="mt-3 text-ink">
        {announcement}
      </p>
    </section>
  )
}
