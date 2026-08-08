/* eslint-disable react-refresh/only-export-components -- primitive re-export module, not a route component */
import * as ToastPrimitive from '@radix-ui/react-toast'
import type { ComponentProps } from 'react'

const rootClasses =
  'rounded-control border-2 border-ink bg-card p-4 font-body text-ink shadow-offset-card'
const viewportClasses = 'fixed bottom-4 right-4 z-50 flex w-full max-w-sm flex-col gap-2 outline-none'

function ToastRoot({
  className,
  ...props
}: ComponentProps<typeof ToastPrimitive.Root>) {
  const classes = [rootClasses, className].filter(Boolean).join(' ')
  return <ToastPrimitive.Root className={classes} {...props} />
}

function ToastViewport({
  className,
  ...props
}: ComponentProps<typeof ToastPrimitive.Viewport>) {
  const classes = [viewportClasses, className].filter(Boolean).join(' ')
  return <ToastPrimitive.Viewport className={classes} {...props} />
}

export const Toast = {
  ...ToastPrimitive,
  Root: ToastRoot,
  Viewport: ToastViewport,
}
