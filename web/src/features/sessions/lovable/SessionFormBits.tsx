import { useRef, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { TFunction } from 'i18next'
import { Button } from '../../../components/ui/button'
import {
  controlRowClasses,
  fieldControlClasses,
  fieldSelectClasses,
  neutralCtaClasses,
} from '../../../components/ui/classes'
import { DateEcho } from '../../../components/ui/dateEcho'
import { Dialog } from '../../../components/ui/dialog'
import { cn } from '../../availability/lovable/utils'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { avatarImgUrl } from '../../friends/avatarImage'
import type { FriendItem } from '../../friends/friendGraph'
import { SteamGamePicker } from '../SteamGamePicker'

/**
 * Shared S2 subcomponents for `SessionCreatePage`/`SessionEditPage`'s
 * Lovable skin — carved out so the two forms cannot drift on the pieces they
 * genuinely share (grammar ported from `../../polls/lovable/PollCreatePage`:
 * `surface`/`paper-grain` sections, a font-display heading with a lucide
 * icon, a `role="radiogroup"` segmented control, `min-h-11` everywhere).
 * Each page still owns its own top-level layout, validation, and submit —
 * only the field-level rendering lives here.
 */

export type SessionFormErrors = Record<string, string>

export function Section({
  icon: Icon,
  heading,
  children,
}: {
  icon: React.ComponentType<{ className?: string }>
  heading: string
  children: ReactNode
}) {
  return (
    <section className="surface paper-grain space-y-3 p-4">
      <h2 className="flex items-center gap-2 font-display text-base font-extrabold text-ink">
        <Icon className="size-4" /> {heading}
      </h2>
      {children}
    </section>
  )
}

export function SegmentedGroup<T extends string>({
  options,
  value,
  onChange,
  ariaLabel,
}: {
  options: { value: T; label: string }[]
  value: T
  onChange: (v: T) => void
  ariaLabel: string
}) {
  return (
    <div
      aria-label={ariaLabel}
      className="grid gap-1 rounded-xl bg-muted p-1"
      role="radiogroup"
      style={{ gridTemplateColumns: `repeat(${options.length}, minmax(0,1fr))` }}
    >
      {options.map((o) => (
        <button
          key={o.value}
          aria-checked={value === o.value}
          className={cn(
            'h-11 rounded-lg font-display text-[13px] font-bold transition-colors',
            value === o.value ? 'bg-card text-ink shadow-[var(--shadow-card)]' : 'text-ink-soft',
          )}
          role="radio"
          type="button"
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

type DiscardDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onDiscard: () => void
  triggerRef: React.RefObject<HTMLElement | null>
}

/**
 * The discard-confirmation contract (D-152/D-162) as its own copy in this
 * namespace — each Lovable surface owns its own dialog rather than reaching
 * across features.
 */
export function SessionDiscardDialog({
  open,
  onOpenChange,
  onDiscard,
  triggerRef,
}: DiscardDialogProps) {
  const { t } = useTranslation('sessions')
  const cancelButton = useRef<HTMLButtonElement>(null)

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-scrim" />
        <Dialog.Content
          aria-describedby="session-discard-dialog-description-lovable"
          aria-label={t('a11y.dialogLabel')}
          className="fixed left-1/2 top-1/2 w-[calc(100%_-_2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-card border-2 border-ink bg-card p-6 shadow-offset-dialog focus:outline-none"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            triggerRef.current?.focus()
          }}
          onOpenAutoFocus={(event) => {
            event.preventDefault()
            cancelButton.current?.focus()
          }}
        >
          <Dialog.Title className="text-h2 font-display font-semibold text-ink">
            {t('dirty.title')}
          </Dialog.Title>
          <Dialog.Description
            className="mt-2 text-ink-soft"
            id="session-discard-dialog-description-lovable"
          >
            {t('dirty.body')}
          </Dialog.Description>
          <div className="mt-6 flex flex-wrap justify-end gap-2">
            <Dialog.Close asChild>
              <button
                ref={cancelButton}
                className={neutralCtaClasses}
                type="button"
              >
                {t('dirty.stay')}
              </button>
            </Dialog.Close>
            <Button type="button" variant="danger" onClick={onDiscard}>
              {t('dirty.discard')}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

export type SessionGameValues = {
  platform: string
  gameName: string
  gameAppId: string
}

/**
 * The Steam-arm game field (D-176/D-194, `SteamGamePicker`) or its
 * free-text sibling for a non-Steam platform. `clearControl`, rendered
 * beneath either arm, is `SessionEditPage`'s "Remove game" checkbox — its
 * own presence (not this component's) is what decides whether the field
 * disables, so `disabled` stays a plain prop rather than baked-in state.
 */
export function SessionGameField({
  t,
  values,
  errors,
  disabled = false,
  clearControl,
  onGameNameChange,
  onGameAppIdChange,
  onGameSelect,
}: {
  t: TFunction
  values: SessionGameValues
  errors: SessionFormErrors
  disabled?: boolean
  clearControl?: ReactNode
  onGameNameChange: (name: string) => void
  onGameAppIdChange: (appId: string) => void
  onGameSelect: (result: { name: string; appId: string }) => void
}) {
  return (
    <fieldset className="flex flex-col gap-1.5">
      <legend className="text-[12px] font-bold text-ink-soft">{t('create.game')}</legend>
      {values.platform === 'Steam' && !disabled ? (
        <SteamGamePicker
          appIdError={errors.gameAppId}
          appIdId="field-game-app-id"
          appIdValue={values.gameAppId}
          nameError={errors.gameName}
          nameId="field-game-name"
          nameValue={values.gameName}
          t={t}
          onAppIdChange={onGameAppIdChange}
          onNameChange={onGameNameChange}
          onSelect={onGameSelect}
        />
      ) : (
        <>
          <label className="block" htmlFor="field-game-name">
            {t('create.gameFreeText')}
          </label>
          <input
            aria-describedby={errors.gameName ? 'game-name-error' : undefined}
            aria-invalid={Boolean(errors.gameName)}
            className={fieldControlClasses}
            disabled={disabled}
            id="field-game-name"
            value={values.gameName}
            onChange={(event) => onGameNameChange(event.target.value)}
          />
          {errors.gameName && (
            <p id="game-name-error" role="alert">
              {errors.gameName}
            </p>
          )}
        </>
      )}
      {clearControl}
    </fieldset>
  )
}

export type SessionDetailsValues = {
  title: string
  durationMinutes: string
  platform: string
  notes: string
}

/**
 * Title / platform / duration / notes — the fields both forms carry
 * verbatim. Start time and the game field stay outside: start renders very
 * differently once a session is locked (`SessionEditPage`'s `edit.startLocked`
 * text has no create-page equivalent), and the game field needs its own
 * `clearControl` slot (see `SessionGameField`).
 */
export function SessionDetailsFieldset({
  t,
  values,
  errors,
  titleReadOnly = false,
  notesDisabled = false,
  showPlatform = true,
  onTitleChange,
  onPlatformChange,
  onDurationChange,
  onNotesChange,
}: {
  t: TFunction
  values: SessionDetailsValues
  errors: SessionFormErrors
  titleReadOnly?: boolean
  notesDisabled?: boolean
  /**
   * The flat `SessionEditPage` never exposed a platform control — the
   * platform is fixed at creation and only read (to pick the game field's
   * arm). `SessionCreatePage` is the only caller that renders this select.
   */
  showPlatform?: boolean
  onTitleChange: (title: string) => void
  onPlatformChange?: (platform: string) => void
  onDurationChange: (durationMinutes: string) => void
  onNotesChange: (notes: string) => void
}) {
  return (
    <>
      <label className="block" htmlFor="field-title">
        <span className="text-[12px] font-bold text-ink-soft">{t('create.title')}</span>
        <input
          aria-describedby={errors.title ? 'title-error' : undefined}
          aria-invalid={Boolean(errors.title)}
          className={fieldControlClasses}
          id="field-title"
          readOnly={titleReadOnly}
          required
          value={values.title}
          onChange={(event) => onTitleChange(event.target.value)}
        />
      </label>
      {errors.title && (
        <p id="title-error" role="alert">
          {errors.title}
        </p>
      )}

      {showPlatform && (
        <label className="block" htmlFor="field-platform">
          <span className="text-[12px] font-bold text-ink-soft">{t('create.platform')}</span>
          <select
            className={fieldSelectClasses}
            id="field-platform"
            value={values.platform}
            onChange={(event) => onPlatformChange?.(event.target.value)}
          >
            <option value="">—</option>
            <option value="Steam">Steam</option>
            <option value="PlayStation">PlayStation</option>
            <option value="Xbox">Xbox</option>
            <option value="Other">{t('create.platformOther')}</option>
          </select>
        </label>
      )}

      <label className="block" htmlFor="field-duration">
        <span className="text-[12px] font-bold text-ink-soft">{t('create.duration')}</span>
        <input
          aria-describedby={errors.durationMinutes ? 'duration-error' : undefined}
          aria-invalid={Boolean(errors.durationMinutes)}
          className={fieldControlClasses}
          id="field-duration"
          min={15}
          step={15}
          type="number"
          value={values.durationMinutes}
          onChange={(event) => onDurationChange(event.target.value)}
        />
      </label>
      {errors.durationMinutes && (
        <p id="duration-error" role="alert">
          {errors.durationMinutes}
        </p>
      )}

      <label className="block" htmlFor="field-notes">
        <span className="text-[12px] font-bold text-ink-soft">{t('create.notes')}</span>
        <textarea
          className={fieldControlClasses}
          disabled={notesDisabled}
          id="field-notes"
          value={values.notes}
          onChange={(event) => onNotesChange(event.target.value)}
        />
      </label>
    </>
  )
}

/**
 * The native `datetime-local` start field, plus its locale echo — shared
 * because both forms use the identical control; only whether it renders at
 * all (vs. `SessionEditPage`'s `edit.startLocked` message once a session is
 * in progress) differs, which stays the caller's own conditional.
 */
export function SessionStartField({
  t,
  value,
  error,
  onChange,
}: {
  t: TFunction
  value: string
  error?: string
  onChange: (value: string) => void
}) {
  return (
    <>
      <label className="block" htmlFor="field-start">
        <span className="text-[12px] font-bold text-ink-soft">{t('create.start')}</span>
        <input
          aria-describedby={error ? 'start-error' : undefined}
          aria-invalid={Boolean(error)}
          className={fieldControlClasses}
          id="field-start"
          step={900}
          type="datetime-local"
          value={value}
          onChange={(event) => onChange(event.target.value)}
        />
      </label>
      <DateEcho value={value} />
      {error && (
        <p id="start-error" role="alert">
          {error}
        </p>
      )}
    </>
  )
}

export type SessionAttendanceValues = {
  minAttendance: string
  maxCapacity: string
  unlimited: boolean
}

/**
 * Minimum / maximum / "no maximum" toggle. The toggle means two different
 * things server-side — create's `unlimited` (D-129 default `maxCapacity: 4`)
 * vs. edit's `clearMax` (A-070's PATCH `clear` list) — but the field shape
 * (a checked box disables the max input) is identical, so it takes a plain
 * `unlimitedLabel` rather than knowing which mode it's in.
 */
export function SessionAttendanceFieldset({
  t,
  values,
  errors,
  unlimitedLabel,
  minLocked = false,
  minLockedMessage,
  onMinChange,
  onMaxChange,
  onUnlimitedChange,
}: {
  t: TFunction
  values: SessionAttendanceValues
  errors: SessionFormErrors
  unlimitedLabel: string
  minLocked?: boolean
  minLockedMessage?: string
  onMinChange: (value: string) => void
  onMaxChange: (value: string) => void
  onUnlimitedChange: (checked: boolean) => void
}) {
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          {minLocked ? (
            <>
              <span className="text-[12px] font-bold text-ink-soft">{t('create.min')}</span>
              <p>{minLockedMessage}</p>
            </>
          ) : (
            <label className="block" htmlFor="field-min">
              <span className="text-[12px] font-bold text-ink-soft">{t('create.min')}</span>
              <input
                aria-describedby={errors.minAttendance ? 'min-error' : undefined}
                aria-invalid={Boolean(errors.minAttendance)}
                className={fieldControlClasses}
                id="field-min"
                min={1}
                type="number"
                value={values.minAttendance}
                onChange={(event) => onMinChange(event.target.value)}
              />
            </label>
          )}
          {errors.minAttendance && (
            <p id="min-error" role="alert">
              {errors.minAttendance}
            </p>
          )}
        </div>
        <div>
          <label className="block" htmlFor="field-max">
            <span className="text-[12px] font-bold text-ink-soft">{t('create.max')}</span>
            <input
              aria-describedby={errors.maxCapacity ? 'max-error' : undefined}
              aria-invalid={Boolean(errors.maxCapacity)}
              className={fieldControlClasses}
              disabled={values.unlimited}
              id="field-max"
              min={1}
              type="number"
              value={values.maxCapacity}
              onChange={(event) => onMaxChange(event.target.value)}
            />
          </label>
          {errors.maxCapacity && (
            <p id="max-error" role="alert">
              {errors.maxCapacity}
            </p>
          )}
        </div>
      </div>
      <label className={controlRowClasses}>
        <input
          checked={values.unlimited}
          type="checkbox"
          onChange={(event) => onUnlimitedChange(event.target.checked)}
        />
        {unlimitedLabel}
      </label>
    </>
  )
}

/**
 * The real two-state `isPrivate` boolean as a two-item segmented group —
 * `PollCreatePage`'s privacy skin (D-125's sessions equivalent is D-129's
 * invitee requirement, enforced by the caller's own validation, not here).
 */
export function SessionPrivacyControl({
  t,
  value,
  onChange,
  disclosure,
}: {
  t: TFunction
  value: boolean
  onChange: (isPrivate: boolean) => void
  disclosure?: ReactNode
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <SegmentedGroup
        ariaLabel={t('create.private')}
        options={[
          { value: 'private' as const, label: t('create.privacyPrivate') },
          { value: 'public' as const, label: t('create.privacyPublic') },
        ]}
        value={value ? 'private' : 'public'}
        onChange={(v) => onChange(v === 'private')}
      />
      {disclosure}
    </div>
  )
}

export function SessionInviteesFieldset({
  t,
  friends,
  selected,
  error,
  onToggle,
}: {
  t: TFunction
  friends: FriendItem[]
  selected: string[]
  error?: string
  onToggle: (userId: string, checked: boolean) => void
}) {
  return (
    <fieldset className="flex flex-col gap-2" id="field-invitees">
      <legend className="text-[12px] font-bold text-ink-soft">{t('create.invitees')}</legend>
      {error && <p role="alert">{error}</p>}
      <ul className="flex flex-col gap-2">
        {friends.map((friend) => (
          <li key={friend.friendshipId}>
            <label className={controlRowClasses}>
              <input
                checked={selected.includes(friend.userId)}
                type="checkbox"
                onChange={(event) => onToggle(friend.userId, event.target.checked)}
              />
              <AvatarDisc
                emoji={friend.avatar.emoji}
                imgUrl={avatarImgUrl(friend.avatar)}
                seed={friend.userId}
                size="xs"
              />
              {friend.displayName} (@{friend.username})
            </label>
          </li>
        ))}
      </ul>
    </fieldset>
  )
}

export function SessionGuestLinksField({
  t,
  value,
  error,
  onChange,
}: {
  t: TFunction
  value: string
  error?: string
  onChange: (value: string) => void
}) {
  return (
    <>
      <label className="block" htmlFor="field-guest-links">
        <span className="text-[12px] font-bold text-ink-soft">{t('create.guestLinks')}</span>
        <input
          aria-describedby={error ? 'guest-links-error' : undefined}
          aria-invalid={Boolean(error)}
          className={fieldControlClasses}
          id="field-guest-links"
          max={20}
          min={0}
          type="number"
          value={value}
          onChange={(event) => onChange(event.target.value)}
        />
      </label>
      {error && (
        <p id="guest-links-error" role="alert">
          {error}
        </p>
      )}
    </>
  )
}

export function ValidationSummary({
  t,
  errors,
  fieldIds,
  summaryRef,
}: {
  t: TFunction
  errors: SessionFormErrors
  fieldIds: Record<string, string>
  summaryRef: React.RefObject<HTMLDivElement | null>
}) {
  if (Object.keys(errors).length === 0) return null
  return (
    <div
      ref={summaryRef}
      className="rounded-card border-2 border-danger bg-danger/10 p-4"
      role="alert"
      tabIndex={-1}
    >
      <p className="font-display font-semibold text-ink">{t('a11y.validationSummary')}</p>
      <ul className="mt-2 list-disc pl-5 text-ink">
        {Object.entries(errors).map(([field, message]) => (
          <li key={field}>
            <a
              className="font-semibold text-ink underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
              href={`#${fieldIds[field] ?? `field-${field}`}`}
            >
              {message}
            </a>
          </li>
        ))}
      </ul>
    </div>
  )
}
