/* eslint-disable react-refresh/only-export-components -- primitive re-export module, not a route component */
import * as DialogPrimitive from '@radix-ui/react-dialog'
import type { ComponentProps } from 'react'

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
    <DialogPrimitive.Content className={className} {...props}>
      <div className="motion-safe:animate-[dialog-pop_120ms_ease-out]">
        {children}
      </div>
    </DialogPrimitive.Content>
  )
}

export const Dialog = {
  ...DialogPrimitive,
  Content: DialogContent,
}
