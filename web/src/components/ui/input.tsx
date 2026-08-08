import type { InputHTMLAttributes } from 'react'

const baseClasses =
  'min-h-11 w-full rounded-control border-2 border-ink bg-card px-3 py-2 font-body text-body text-ink placeholder:text-ink-soft focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:pointer-events-none disabled:opacity-50'

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  const classes = [baseClasses, className].filter(Boolean).join(' ')

  return <input className={classes} {...props} />
}
