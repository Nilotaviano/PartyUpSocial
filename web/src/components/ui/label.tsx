import type { LabelHTMLAttributes } from 'react'

const baseClasses = 'font-body text-body font-semibold text-ink'

export function Label({ className, ...props }: LabelHTMLAttributes<HTMLLabelElement>) {
  const classes = [baseClasses, className].filter(Boolean).join(' ')

  return <label className={classes} {...props} />
}
