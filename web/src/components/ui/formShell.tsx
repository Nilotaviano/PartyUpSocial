import type { FormEventHandler, ReactNode } from 'react'

/**
 * The single-purpose form column (STYLING_OVERHAUL_DESIGN.md §3.1): one
 * `max-w-form` SectionCard holding the fields, with the primary action right
 * and a ghost cancel left of it.
 *
 * The action nodes are slots rather than props because the shipped pages own
 * them: the cancel button carries the dirty-guard ref the discard dialog
 * restores focus to, and the submit button carries the mutation's busy state.
 * FormShell places them; it never re-creates them.
 */
export interface FormShellProps {
  children: ReactNode
  onSubmit: FormEventHandler<HTMLFormElement>
  submit: ReactNode
  cancel?: ReactNode
  /** Page-scoped nodes that must sit above the fields (validation summary, banners). */
  header?: ReactNode
  className?: string
}

/**
 * The form column's card recipe, exported for the rare form that must own its
 * own `<form>` element (a form whose only validation is the native constraint
 * FormShell's `noValidate` would switch off).
 */
export const formShellClasses =
  'flex flex-col gap-6 rounded-card border-2 border-ink bg-card p-5 shadow-offset-card'

export function FormShell({
  children,
  onSubmit,
  submit,
  cancel,
  header,
  className,
}: FormShellProps) {
  const classes = [formShellClasses, className]
    .filter(Boolean)
    .join(' ')

  return (
    <form className={classes} noValidate onSubmit={onSubmit}>
      {header}
      {children}
      <div className="flex flex-wrap items-center gap-2">
        {cancel}
        <span className="ml-auto">{submit}</span>
      </div>
    </form>
  )
}

/**
 * One labelled field: label, its own helper sentence, then the control. The
 * helper text is per-field by design — it replaces the page-level "defaults"
 * paragraph the audit flagged (F12), so the guidance sits where the decision
 * is made.
 *
 * The hint renders at `${htmlFor}-hint`; callers that want it announced point
 * their control's `aria-describedby` at that id (kept caller-side because the
 * shipped controls already compose `aria-describedby` with their error ids).
 */
export interface FormFieldProps {
  children: ReactNode
  label: ReactNode
  /** Omitted for grouped controls that label themselves (fieldset/legend). */
  htmlFor?: string
  hint?: ReactNode
  className?: string
}

export function FormField({
  children,
  label,
  htmlFor,
  hint,
  className,
}: FormFieldProps) {
  const classes = ['flex flex-col gap-1.5', className].filter(Boolean).join(' ')

  return (
    <div className={classes}>
      <label className="font-body font-semibold text-ink" htmlFor={htmlFor}>
        {label}
      </label>
      {hint && (
        <p className="font-body text-meta text-ink-soft" id={htmlFor ? `${htmlFor}-hint` : undefined}>
          {hint}
        </p>
      )}
      {children}
    </div>
  )
}
