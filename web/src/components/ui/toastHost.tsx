import { useCallback, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { raisedInteractiveClasses } from './classes'
import { Toast } from './toast'
import { ToastDispatchContext, type ToastRequest } from './toastStore'

interface QueuedToast extends ToastRequest {
  id: number
}

const actionClasses = `inline-flex min-h-11 items-center bg-card px-3 text-ink ${raisedInteractiveClasses}`

/**
 * The shell's toast queue (STYLING_OVERHAUL_DESIGN.md §3.5). Toasts are
 * additive visual chrome: the page's own live region stays the announcement
 * source of truth, and a toast never carries the only copy of a message.
 */
export function ToastHost({ children }: { children: ReactNode }) {
  const { t } = useTranslation('common')
  const [queue, setQueue] = useState<QueuedToast[]>([])
  const nextId = useRef(0)

  const show = useCallback((toast: ToastRequest) => {
    nextId.current += 1
    setQueue((current) => [...current, { ...toast, id: nextId.current }])
  }, [])

  const dismiss = useCallback((id: number) => {
    setQueue((current) => current.filter((toast) => toast.id !== id))
  }, [])

  return (
    <ToastDispatchContext.Provider value={show}>
      <Toast.Provider label={t('toast.regionLabel')} swipeDirection="right">
        {children}
        {queue.map((toast) => (
          <Toast.Root
            key={toast.id}
            open
            onOpenChange={(open) => {
              if (!open) dismiss(toast.id)
            }}
          >
            <div className="flex flex-wrap items-center gap-3">
              <Toast.Description className="min-w-0 flex-1">
                {toast.message}
              </Toast.Description>
              {toast.action && (
                <Toast.Action
                  altText={toast.action.label}
                  className={actionClasses}
                  onClick={toast.action.onAction}
                >
                  {toast.action.label}
                </Toast.Action>
              )}
              <Toast.Close
                aria-label={t('toast.dismissLabel')}
                className="inline-flex min-h-11 min-w-11 flex-none items-center justify-center rounded-control text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
              >
                <span aria-hidden="true">✕</span>
              </Toast.Close>
            </div>
          </Toast.Root>
        ))}
        <Toast.Viewport label={t('toast.regionLabel')} />
      </Toast.Provider>
    </ToastDispatchContext.Provider>
  )
}
