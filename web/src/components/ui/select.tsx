/* eslint-disable react-refresh/only-export-components -- primitive re-export module, not a route component */
import * as SelectPrimitive from '@radix-ui/react-select'
import type { ComponentProps } from 'react'

const triggerClasses =
  'inline-flex min-h-11 w-full items-center justify-between gap-2 rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:pointer-events-none disabled:opacity-50'
const contentClasses =
  'overflow-hidden rounded-control border-2 border-ink bg-card font-body text-ink shadow-offset-card'
const viewportClasses = 'p-1'
const itemClasses =
  'flex min-h-11 cursor-pointer items-center rounded-[0.375rem] px-3 py-2 outline-none data-[highlighted]:bg-accent data-[highlighted]:text-accent-ink'

function SelectTrigger({
  className,
  ...props
}: ComponentProps<typeof SelectPrimitive.Trigger>) {
  const classes = [triggerClasses, className].filter(Boolean).join(' ')
  return <SelectPrimitive.Trigger className={classes} {...props} />
}

function SelectContent({
  className,
  ...props
}: ComponentProps<typeof SelectPrimitive.Content>) {
  const classes = [contentClasses, className].filter(Boolean).join(' ')
  return <SelectPrimitive.Content className={classes} {...props} />
}

function SelectViewport({
  className,
  ...props
}: ComponentProps<typeof SelectPrimitive.Viewport>) {
  const classes = [viewportClasses, className].filter(Boolean).join(' ')
  return <SelectPrimitive.Viewport className={classes} {...props} />
}

function SelectItem({
  className,
  ...props
}: ComponentProps<typeof SelectPrimitive.Item>) {
  const classes = [itemClasses, className].filter(Boolean).join(' ')
  return <SelectPrimitive.Item className={classes} {...props} />
}

export const Select = {
  ...SelectPrimitive,
  Trigger: SelectTrigger,
  Content: SelectContent,
  Viewport: SelectViewport,
  Item: SelectItem,
}
