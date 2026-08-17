import { useRef, useState, type ComponentType, type ReactNode } from 'react'
import { Check } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import {
  chipClasses,
  fieldControlClasses,
  neutralCtaClasses,
  quietChipClasses,
} from '../../components/ui/classes'
import { Dialog } from '../../components/ui/dialog'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import type { FriendItem } from '../friends/friendGraph'

/**
 * Ported from the Lovable reference (`src/components/groups/GroupBits.tsx`)
 * — the four pieces Task 10 owns (AvatarStack/Tag/FriendMultiSelect/
 * NewGroupDialog). `VisibilityControl`/`ShareTag` stay out of this file;
 * they are Task 11's (session/poll visibility, provenance tags).
 *
 * Adaptations from the prototype (the only sanctioned deviations):
 *  - The prototype's own `Avatar` (initials-on-a-chip) is dropped for the
 *    repo's existing `AvatarDisc` (`features/friends/AvatarDisc`) — never
 *    reinvented here.
 *  - The prototype resolves every id through an in-memory `person()`
 *    directory. `AvatarStack` instead takes the server-resolved roster
 *    people directly (`FriendGroupMemberResponse.displayName`/`avatar`/
 *    `masked`, plus the detail response's owner nameplate) — a D-076-masked
 *    entry (a blocked-pair counterpart, per D-202) renders through the same
 *    ordinary "masked" `AvatarDisc` treatment every other masked surface
 *    uses, with no client-side friends-list join involved.
 *  - Dialogs follow the repo's `SessionDiscardDialog` contract (Radix,
 *    `bg-scrim` overlay, `rounded-card border-2 border-ink`, explicit
 *    `onOpenAutoFocus`/`onCloseAutoFocus`) rather than the prototype's
 *    shadcn `Dialog`/`Input`/`Label`. Submits use the repo `Button` (its
 *    `variant="primary"` already carries the raised treatment); every
 *    string routes through `t()` (namespace `groups`).
 */

function useGroupsTranslation() {
  return useTranslation('groups')
}

/** One roster person's server-resolved identity — enough to render a disc. */
export type StackPerson = {
  userId: string
  masked: boolean
  avatar?: { emoji?: string | null; imgUrl?: string | null } | null
}

/** Overlapping avatars with a +N overflow disc (max 5, `-space-x-2`). */
export function AvatarStack({ people, max = 5 }: { people: StackPerson[]; max?: number }) {
  const { t } = useGroupsTranslation()
  const shown = people.slice(0, max)
  const rest = people.length - shown.length

  return (
    <span className="flex items-center">
      <span className="flex -space-x-2">
        {shown.map((person) => (
          <AvatarDisc
            key={person.userId}
            className="ring-2 ring-card"
            emoji={person.avatar?.emoji}
            imgUrl={avatarImgUrl(person.avatar)}
            masked={person.masked}
            seed={person.userId}
            size="xs"
          />
        ))}
        {rest > 0 && (
          <span className="grid size-9 shrink-0 place-items-center rounded-full border-2 border-ink bg-muted font-display text-[11px] font-bold text-ink-soft ring-2 ring-card tabular-nums">
            +{rest}
          </span>
        )}
      </span>
      <span className="sr-only">{t('roster.memberCount', { count: people.length })}</span>
    </span>
  )
}

/** Small icon + text tag — colour is never the only signal. */
export function Tag({
  children,
  icon: Icon,
  tone = 'neutral',
}: {
  children: ReactNode
  icon?: ComponentType<{ className?: string }>
  tone?: 'neutral' | 'accent'
}) {
  return (
    <span className={tone === 'accent' ? chipClasses : quietChipClasses}>
      {Icon && <Icon aria-hidden className="size-3" />}
      {children}
    </span>
  )
}

/** Accessible checkbox list of friend candidates. */
export function FriendMultiSelect({
  candidates,
  selected,
  onToggle,
  legend,
}: {
  candidates: FriendItem[]
  selected: string[]
  onToggle: (userId: string) => void
  legend: string
}) {
  const { t } = useGroupsTranslation()

  if (candidates.length === 0) {
    return (
      <p className="rounded-xl border border-dashed border-border bg-muted/40 px-3 py-4 text-center text-[12.5px] text-ink-soft">
        {t('multiSelect.allInvited')}
      </p>
    )
  }

  return (
    <fieldset>
      <legend className="mb-1.5 font-display text-[12px] font-bold text-ink-soft">
        {legend}
      </legend>
      <ul className="max-h-64 space-y-1 overflow-y-auto pr-1">
        {candidates.map((friend) => {
          const on = selected.includes(friend.userId)
          return (
            <li key={friend.userId}>
              <label
                className={[
                  'flex min-h-11 cursor-pointer items-center gap-3 rounded-xl border-2 px-3 py-2',
                  on ? 'border-primary bg-primary/15' : 'border-ink bg-card',
                ].join(' ')}
              >
                <input
                  checked={on}
                  className="size-5 accent-primary"
                  type="checkbox"
                  onChange={() => onToggle(friend.userId)}
                />
                <AvatarDisc
                  emoji={friend.avatar.emoji}
                  imgUrl={avatarImgUrl(friend.avatar)}
                  seed={friend.userId}
                  size="xs"
                />
                <span className="min-w-0 flex-1 truncate font-display text-[13.5px] font-bold text-ink">
                  {friend.displayName}
                </span>
                {on && <Check aria-hidden className="size-4 shrink-0 text-ink" />}
              </label>
            </li>
          )
        })}
      </ul>
    </fieldset>
  )
}

type NewGroupDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onCreate: (name: string, memberIds: string[]) => void
  candidates: FriendItem[]
  triggerRef: React.RefObject<HTMLElement | null>
}

/** "New group" dialog: name (<=50 chars) + friend multi-select (D-198). */
export function NewGroupDialog({
  open,
  onOpenChange,
  onCreate,
  candidates,
  triggerRef,
}: NewGroupDialogProps) {
  const { t } = useGroupsTranslation()
  const nameField = useRef<HTMLInputElement>(null)
  const [name, setName] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [touched, setTouched] = useState(false)
  const invalid = name.trim().length === 0

  function close() {
    onOpenChange(false)
    setName('')
    setPicked([])
    setTouched(false)
  }

  return (
    <Dialog.Root open={open} onOpenChange={(next) => (next ? onOpenChange(true) : close())}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="new-group-dialog-description"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            nameField.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('newGroup.title')}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-ink-soft" id="new-group-dialog-description">
            {t('newGroup.description')}
          </Dialog.Description>

          <div className="mt-4 space-y-4">
            <div className="space-y-1.5">
              <label className="font-display text-[12px] font-bold text-ink" htmlFor="new-group-name">
                {t('newGroup.nameLabel')}
              </label>
              <input
                aria-describedby="new-group-name-hint"
                aria-invalid={touched && invalid ? true : undefined}
                className={fieldControlClasses}
                id="new-group-name"
                maxLength={50}
                placeholder={t('newGroup.namePlaceholder')}
                ref={nameField}
                value={name}
                onBlur={() => setTouched(true)}
                onChange={(event) => setName(event.target.value)}
              />
              <p
                className={touched && invalid ? 'text-[12px] font-semibold text-danger' : 'text-[12px] text-ink-soft'}
                id="new-group-name-hint"
              >
                {touched && invalid
                  ? t('newGroup.nameRequired')
                  : t('newGroup.nameCount', { count: name.length })}
              </p>
            </div>

            <FriendMultiSelect
              candidates={candidates}
              legend={t('newGroup.inviteLegend')}
              selected={picked}
              onToggle={(userId) =>
                setPicked((current) =>
                  current.includes(userId)
                    ? current.filter((id) => id !== userId)
                    : [...current, userId],
                )
              }
            />
          </div>

          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <button className={neutralCtaClasses} type="button" onClick={close}>
              {t('actions.cancel')}
            </button>
            <Button
              disabled={invalid}
              type="button"
              variant="primary"
              onClick={() => {
                onCreate(name.trim(), picked)
                close()
              }}
            >
              {t('newGroup.submit')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
