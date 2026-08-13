import type { ButtonHTMLAttributes } from 'react'

export type ButtonVariant = 'primary' | 'neutral' | 'danger' | 'ghost'

const baseClasses =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-control px-4 py-2 font-semibold motion-safe:transition-transform motion-safe:motion-press motion-safe:ease-out focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:pointer-events-none disabled:opacity-50'

// The 2px press travel is the offset-shadow delta (`--shadow-offset-control`
// 3px → `--shadow-offset-pressed` 1px), not a spacing step — the 4px scale has
// no token for it, so it stays an arbitrary value here and in `classes.ts`.
const raisedClasses =
  'border-2 font-display shadow-offset-control active:translate-x-[2px] active:translate-y-[2px] active:shadow-offset-pressed'

const variantClasses: Record<ButtonVariant, string> = {
  primary: `${raisedClasses} border-ink bg-primary text-primary-ink`,
  neutral: `${raisedClasses} border-ink bg-card text-ink`,
  danger: `${raisedClasses} border-ink bg-danger text-danger-ink dark:border-danger-ink`,
  ghost: 'border-2 border-transparent bg-transparent font-display text-ink',
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
}

export function Button({ variant = 'neutral', className, ...props }: ButtonProps) {
  const classes = [baseClasses, variantClasses[variant], className]
    .filter(Boolean)
    .join(' ')

  return <button type="button" className={classes} {...props} />
}
