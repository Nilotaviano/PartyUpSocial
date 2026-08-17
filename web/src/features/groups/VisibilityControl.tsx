import { Check, Globe2, Lock, UsersRound, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '../availability/lovable/utils'

/**
 * Ported from the Lovable reference (`src/components/groups/GroupBits.tsx`'s
 * `VisibilityControl` export) onto the repo's `SegmentedGroup` idiom (a
 * `role="radiogroup"` of `role="radio"` buttons in a `bg-muted` strip,
 * `h-11` targets) as a self-contained 3-column variant — kept local rather
 * than importing `SessionFormBits.tsx`'s copy so this `groups`-feature
 * component never reaches into the `sessions` feature.
 *
 * The group chip picker (shown only when `value === 'groups'`) is a toggle
 * button list, `aria-pressed` per chip (Check when selected, X to remove),
 * fed by the caller from `groupKeys.list`'s owned + member-of groups — any
 * group the actor owns or holds an Accepted membership in can target
 * (D-201). The zero-selection state renders a destructive hint inline
 * (`errorMessage`) in addition to whatever the caller's own field-error
 * machinery surfaces above the control.
 */

export type Visibility = 'private' | 'groups' | 'public'
export type VisibilityGroupOption = { id: string; name: string }

export function VisibilityControl({
  value,
  onChange,
  groups,
  selectedGroupIds,
  onToggleGroup,
  idPrefix = 'visibility',
}: {
  value: Visibility
  onChange: (value: Visibility) => void
  groups: VisibilityGroupOption[]
  selectedGroupIds: string[]
  onToggleGroup: (groupId: string) => void
  idPrefix?: string
}) {
  const { t } = useTranslation('groups')

  const options: { value: Visibility; label: string; icon: typeof Lock }[] = [
    { value: 'private', label: t('visibilityControl.private'), icon: Lock },
    { value: 'groups', label: t('visibilityControl.groups'), icon: UsersRound },
    { value: 'public', label: t('visibilityControl.public'), icon: Globe2 },
  ]

  const unpicked = value === 'groups' && selectedGroupIds.length === 0

  return (
    <div className="space-y-2">
      <div
        aria-label={t('visibilityControl.ariaLabel')}
        className="grid grid-cols-3 gap-1 rounded-xl bg-muted p-1"
        role="radiogroup"
      >
        {options.map((option) => (
          <button
            key={option.value}
            aria-checked={value === option.value}
            className={cn(
              'inline-flex h-11 items-center justify-center gap-1.5 rounded-lg font-display text-[13px] font-bold transition-colors',
              value === option.value
                ? 'bg-card text-ink shadow-[var(--shadow-card)]'
                : 'text-ink-soft',
            )}
            role="radio"
            type="button"
            onClick={() => onChange(option.value)}
          >
            <option.icon aria-hidden className="size-4" />
            {option.label}
          </button>
        ))}
      </div>

      {value === 'groups' && (
        <div className="rounded-xl border-2 border-ink bg-card p-3">
          <p className="font-display text-[12px] font-bold text-ink-soft">
            {t('visibilityControl.visibleTo')}
          </p>
          <ul className="mt-2 flex flex-wrap gap-1.5">
            {groups.map((group) => {
              const on = selectedGroupIds.includes(group.id)
              return (
                <li key={group.id}>
                  <button
                    aria-pressed={on}
                    className={cn(
                      'inline-flex h-11 items-center gap-1.5 rounded-xl border-2 px-3 font-display text-[12.5px] font-bold',
                      on ? 'border-primary bg-primary/15 text-ink' : 'border-ink bg-muted text-ink-soft',
                    )}
                    id={`${idPrefix}-${group.id}`}
                    type="button"
                    onClick={() => onToggleGroup(group.id)}
                  >
                    {on ? <X aria-hidden className="size-3.5" /> : <Check aria-hidden className="size-3.5 opacity-40" />}
                    {group.name}
                  </button>
                </li>
              )
            })}
          </ul>
          <p className={cn('mt-2 text-[12px]', unpicked ? 'font-semibold text-danger' : 'text-ink-soft')}>
            {unpicked
              ? t('visibilityControl.pickAtLeastOne')
              : t('visibilityControl.selectedCount', { count: selectedGroupIds.length })}
          </p>
        </div>
      )}
    </div>
  )
}
