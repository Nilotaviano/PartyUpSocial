/* eslint-disable react-refresh/only-export-components -- primitive re-export module, not a route component */
import * as CheckboxPrimitive from '@radix-ui/react-checkbox'
import type { ComponentProps } from 'react'

const rootClasses =
  'inline-flex size-6 shrink-0 items-center justify-center rounded-[0.375rem] border-2 border-ink bg-card text-primary-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus data-[state=checked]:bg-primary disabled:pointer-events-none disabled:opacity-50'

function CheckboxRoot({
  className,
  children,
  ...props
}: ComponentProps<typeof CheckboxPrimitive.Root>) {
  const classes = [rootClasses, className].filter(Boolean).join(' ')

  return (
    <CheckboxPrimitive.Root className={classes} {...props}>
      <CheckboxPrimitive.Indicator className="flex items-center justify-center text-current">
        {children ?? (
          <svg
            aria-hidden="true"
            className="size-4"
            fill="none"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={3}
            viewBox="0 0 24 24"
          >
            <path d="M5 13l4 4L19 7" />
          </svg>
        )}
      </CheckboxPrimitive.Indicator>
    </CheckboxPrimitive.Root>
  )
}

export const Checkbox = {
  ...CheckboxPrimitive,
  Root: CheckboxRoot,
}
