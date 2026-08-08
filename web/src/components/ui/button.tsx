import type { ButtonHTMLAttributes } from 'react'

// 'legacy' is the default on purpose: ~50 pre-Phase-5.1 call sites still pass
// their own bg/text classes, and injecting token fills under them creates AA
// contrast failures (e.g. text-red-800 on bg-primary). Slices S2-S5 migrate
// call sites to explicit variants; 'legacy' dies when the last one converts.
export type ButtonVariant = 'primary' | 'neutral' | 'danger' | 'ghost' | 'legacy'

const baseClasses =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-control px-4 py-2 font-semibold motion-safe:transition-transform motion-safe:duration-75 motion-safe:ease-out focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:pointer-events-none disabled:opacity-50'

const raisedClasses =
  'border-2 font-display shadow-offset-control active:translate-x-[2px] active:translate-y-[2px] active:shadow-offset-pressed'

const variantClasses: Record<ButtonVariant, string> = {
  primary: `${raisedClasses} border-ink bg-primary text-primary-ink`,
  neutral: `${raisedClasses} border-ink bg-card text-ink`,
  danger: `${raisedClasses} border-ink bg-danger text-danger-ink dark:border-danger-ink`,
  ghost: 'border-2 border-transparent bg-transparent font-display text-ink',
  legacy: '',
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
}

export function Button({ variant = 'legacy', className, ...props }: ButtonProps) {
  const classes = [baseClasses, variantClasses[variant], className]
    .filter(Boolean)
    .join(' ')

  return <button type="button" className={classes} {...props} />
}
