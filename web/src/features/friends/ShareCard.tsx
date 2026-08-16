import { useQuery } from '@tanstack/react-query'
import { QrCode } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import QRCodeGraphic from 'react-qr-code'
import { client, isApiError } from '../../api/client'
import { Button } from '../../components/ui/button'
import { SectionCard } from '../../components/shell/SectionCard'
import { useToast } from '../../components/ui/toastStore'

// PHASE5_1_DESIGN.md §1/§3: QR modules always render a dark module ink on a
// light --color-qr chip in both themes (scannability; --color-qr is
// theme-invariant and never overridden under [data-theme=dark]) — both are
// hardcoded literal hex here since react-qr-code needs literal color values,
// not CSS var references, and the module ink has no --color-qr-ink token.
const QR_CHIP_COLOR = '#FFFDF5' // --color-qr
const QR_MODULE_COLOR = '#1B2408'

async function getFriendShare() {
  const { data } = await client.GET('/api/v1/friend-share')
  if (!data) throw new Error('Friend-share response was empty.')
  return data
}

/**
 * A shareable link and the button that copies it, on one line
 * (STYLING_OVERHAUL_DESIGN.md §3.3): the link column is the only one allowed to
 * shrink, and it truncates in CSS so the anchor's accessible name stays the
 * whole URL.
 */
function ShareLinkRow({
  description,
  url,
  action,
}: {
  description: ReactNode
  url: string
  action: ReactNode
}) {
  return (
    <div className="flex flex-col gap-1">
      <p className="text-meta text-ink-soft">{description}</p>
      <div className="grid grid-cols-[1fr_auto] items-center gap-2">
        <a
          className="flex min-h-11 min-w-0 items-center rounded-control text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          href={url}
        >
          <span className="truncate">{url}</span>
        </a>
        {action}
      </div>
    </div>
  )
}

export function ShareCard() {
  const { t } = useTranslation(['friends', 'errors'])
  const [announcement, setAnnouncement] = useState('')
  const toast = useToast()
  const share = useQuery({
    queryKey: ['friend-share'],
    queryFn: getFriendShare,
  })

  async function copy(value: string, label: string) {
    try {
      await navigator.clipboard.writeText(value)
      // The live region stays the announcement source of truth (§3.5); the
      // toast is the additive visible half of the same confirmation.
      setAnnouncement(t('live.copied', { label }))
      toast({ message: t('live.copied', { label }) })
    } catch {
      setAnnouncement(t('generic', { ns: 'errors' }))
    }
  }

  return (
    // `id` is the anchor target `FriendList`'s empty-state CTA relies on —
    // the shell `SectionCard` doesn't expose an id prop to attach to its own
    // h2, so the id lives on this wrapper instead; `role`/`aria-label` give
    // the region a landmark name, matching `FriendsPage.tsx`'s sibling
    // wrappers.
    <div aria-label={t('sections.share')} id="friend-share-heading" role="region">
      <SectionCard icon={QrCode} title={t('sections.share')}>
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
          <div className="grid gap-6 sm:grid-cols-[auto_1fr] sm:items-start">
            <div aria-hidden="true" className="w-fit rounded-xl bg-qr p-6">
              <QRCodeGraphic
                aria-hidden="true"
                bgColor={QR_CHIP_COLOR}
                fgColor={QR_MODULE_COLOR}
                size={152}
                value={share.data.addUrl}
              />
            </div>
            <div className="flex min-w-0 flex-col gap-5">
              <p className="text-ink-soft">{t('qr.description')}</p>
              <div className="flex flex-col gap-2">
                <p className="text-meta text-ink-soft">{t('labels.friendCode')}</p>
                <p className="select-all font-mono text-h2 font-bold tracking-widest text-ink">
                  {share.data.friendCode}
                </p>
                <Button
                  className="self-start"
                  variant="neutral"
                  onClick={() =>
                    void copy(share.data.friendCode, t('labels.friendCode'))
                  }
                >
                  {t('actions.copyCode')}
                </Button>
              </div>
              <ShareLinkRow
                action={
                  <Button
                    variant="neutral"
                    onClick={() =>
                      void copy(share.data.addUrl, t('actions.copyAddLink'))
                    }
                  >
                    {t('actions.copyAddLink')}
                  </Button>
                }
                description={t('share.addLinkDescription')}
                url={share.data.addUrl}
              />
              <ShareLinkRow
                action={
                  <Button
                    variant="neutral"
                    onClick={() =>
                      void copy(share.data.profileUrl, t('actions.copyProfileLink'))
                    }
                  >
                    {t('actions.copyProfileLink')}
                  </Button>
                }
                description={t('share.profileLinkDescription')}
                url={share.data.profileUrl}
              />
            </div>
          </div>
        )}
        <p aria-live="polite" className="text-ink">
          {announcement}
        </p>
      </SectionCard>
    </div>
  )
}
