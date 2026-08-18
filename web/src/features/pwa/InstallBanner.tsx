import { Download } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { NoticeBanner } from '../../components/shell/NoticeBanner'
import { IosInstallDialog } from './IosInstallDialog'
import { useInstallPrompt } from './useInstallPrompt'

/**
 * D-220's first-visit install promotion, mounted once at the app root above every route (App.tsx),
 * so the shell-less surfaces — guest RSVP/vote pages, the age gate, /auth/*, the unsubscribe page —
 * promote it too; guests are the population most likely to install. It never prompts on its own —
 * only the user's own click reaches `promptInstall()` — and a dismiss is persisted per browser, so
 * the banner is offered once and the AccountMenu row remains the way back to it.
 */
export function InstallBanner() {
  const { t } = useTranslation('pwa')
  const {
    canPromptNative,
    isIos,
    standalone,
    dismissed,
    dismiss,
    promptInstall,
  } = useInstallPrompt()
  const [iosOpen, setIosOpen] = useState(false)

  if (standalone || dismissed || !(canPromptNative || isIos)) {
    return null
  }

  return (
    <div
      className="mx-auto w-full max-w-page px-4 pt-4 sm:px-8"
      data-testid="install-banner"
    >
      <NoticeBanner
        actionLabel={t('install.action')}
        body={t('install.body')}
        dismissLabel={t('install.dismiss')}
        icon={Download}
        title={t('install.title')}
        onAction={() => {
          if (canPromptNative) {
            void promptInstall()
            return
          }
          setIosOpen(true)
        }}
        onDismiss={dismiss}
      />
      <IosInstallDialog open={iosOpen} onOpenChange={setIosOpen} />
    </div>
  )
}
