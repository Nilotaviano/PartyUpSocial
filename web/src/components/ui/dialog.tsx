/* eslint-disable react-refresh/only-export-components -- primitive re-export module, not a route component */
import * as DialogPrimitive from '@radix-ui/react-dialog'
import type { ComponentProps } from 'react'

/**
 * Radix portals a dialog to `document.body`, so it competes in the root
 * stacking context with positioned page chrome — `WeekGrid`'s sticky headers
 * sit at `z-20`/`z-10` there. A dialog that declares no layer resolves to
 * `z-index: auto` and loses to them, so the layer is applied here rather than
 * at each call site. An explicit `z-*` from the caller wins.
 */
function withLayer(className: string | undefined, layer: string) {
  const hasOwnLayer = className?.split(/\s+/).some((token) => token.startsWith('z-'))
  return hasOwnLayer ? className : [layer, className].filter(Boolean).join(' ')
}

function DialogOverlay({
  className,
  ...props
}: ComponentProps<typeof DialogPrimitive.Overlay>) {
  return <DialogPrimitive.Overlay className={withLayer(className, 'z-40')} {...props} />
}

/**
 * The pop animation lives on an inner wrapper, never on Content itself:
 * Content carries each call site's own positioning classes (including the
 * centered dialog's `-translate-x-1/2 -translate-y-1/2`, which
 * AuthMethods.test.tsx asserts unchanged).
 */
function DialogContent({
  className,
  children,
  ...props
}: ComponentProps<typeof DialogPrimitive.Content>) {
  return (
    <DialogPrimitive.Content className={withLayer(className, 'z-50')} {...props}>
      <div className="motion-safe:animate-[dialog-pop_120ms_ease-out]">
        {children}
      </div>
    </DialogPrimitive.Content>
  )
}

export const Dialog = {
  ...DialogPrimitive,
  Overlay: DialogOverlay,
  Content: DialogContent,
}
