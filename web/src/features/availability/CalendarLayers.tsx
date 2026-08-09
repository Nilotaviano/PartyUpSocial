import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { components } from '../../api/schema'

export type MaskedParticipant = components['schemas']['MaskedParticipant']

export type CalendarLayerState = {
  myAvailability: boolean
  friendAvailability: boolean
  sessions: boolean
  pollOptions: boolean
  /** Ordinals of the friends currently shown (subset of `friends`). */
  shownFriendOrdinals: Set<number>
}

export type CalendarPollItemSummary = {
  pollId: string
  title: string
}

type CalendarLayersProps = {
  /** The owners present in the current `friendAvailability` response
   * (§8.1 `MaskedParticipant` — ordinal only, never an id). */
  friends: MaskedParticipant[]
  /** Phase 6 S7 fill (design §5/§6.3): the polls whose options are in range, the same
   * `CalendarPollOptionView` list the grid/agenda paint from — a summary list here, never a
   * second fetch. Defaults to empty so every S5/S6 caller keeps compiling untouched. */
  pollItems?: CalendarPollItemSummary[]
  onChange: (state: CalendarLayerState) => void
}

/**
 * D-111's four layer toggles, plus D-117's per-friend filter. D-135 is
 * implemented as the *absence* of persistence: every value below is plain
 * component state, so it starts fresh on every mount with no server call,
 * no `localStorage`, and no URL default (design §5.1/A-080) — proven by
 * `CalendarLayers.test.tsx`'s no-write assertion.
 */
export function CalendarLayers({ friends, pollItems = [], onChange }: CalendarLayersProps) {
  const { t } = useTranslation('availability')
  const [myAvailability, setMyAvailability] = useState(true)
  const [friendAvailability, setFriendAvailability] = useState(true)
  const [sessions, setSessions] = useState(true)
  const [pollOptions, setPollOptions] = useState(true)
  // Excluded, not included: an empty set means every friend is shown, so
  // D-117's "all friends by default" holds the instant `friends` arrives —
  // no effect is needed to sync a default selection onto async data.
  const [excludedOrdinals, setExcludedOrdinals] = useState<Set<number>>(new Set())

  const shownFriendOrdinals = new Set(
    friends
      .map((friend) => Number(friend.ordinal))
      .filter((ordinal) => !excludedOrdinals.has(ordinal)),
  )

  useEffect(() => {
    onChange({
      myAvailability,
      friendAvailability,
      sessions,
      pollOptions,
      shownFriendOrdinals,
    })
    // `shownFriendOrdinals` is derived fresh every render from `friends` and
    // `excludedOrdinals`; comparing its members (not the Set identity) is
    // what the exhaustive-deps rule can't express, so it is intentionally
    // left off — `excludedOrdinals` and `friends` already cover every case
    // that changes it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [myAvailability, friendAvailability, sessions, pollOptions, excludedOrdinals, friends])

  function toggleFriend(ordinal: number, shown: boolean) {
    setExcludedOrdinals((current) => {
      const next = new Set(current)
      if (shown) next.delete(ordinal)
      else next.add(ordinal)
      return next
    })
  }

  return (
    <fieldset className="space-y-3">
      <legend className="font-display font-medium text-ink">{t('calendar.layers.heading')}</legend>
      <label className="flex min-h-11 items-center gap-2">
        <input
          checked={myAvailability}
          type="checkbox"
          onChange={(event) => setMyAvailability(event.target.checked)}
        />
        <span aria-hidden="true" className="h-3 w-3 rounded-full border border-ink/40 bg-free" />
        {t('calendar.layers.myAvailability')}
      </label>
      <label className="flex min-h-11 items-center gap-2">
        <input
          checked={friendAvailability}
          type="checkbox"
          onChange={(event) => setFriendAvailability(event.target.checked)}
        />
        <span aria-hidden="true" className="h-3 w-3 rounded-full border border-ink/40 bg-ink" />
        {t('calendar.layers.friendAvailability')}
      </label>
      <label className="flex min-h-11 items-center gap-2">
        <input
          checked={sessions}
          type="checkbox"
          onChange={(event) => setSessions(event.target.checked)}
        />
        <span aria-hidden="true" className="h-3 w-3 rounded-full border border-ink/40 bg-free-ink" />
        {t('calendar.layers.sessions')}
      </label>
      <label className="flex min-h-11 items-center gap-2">
        <input
          checked={pollOptions}
          type="checkbox"
          onChange={(event) => setPollOptions(event.target.checked)}
        />
        <span aria-hidden="true" className="h-3 w-3 rounded-full border border-ink/40 bg-maybe" />
        {t('calendar.layers.pollOptions')}
      </label>

      {pollOptions && pollItems.length > 0 && (
        <ul className="ml-6 space-y-1 border-l border-ink/25 pl-4 text-sm text-ink-soft">
          {pollItems.map((poll) => (
            <li key={poll.pollId}>{poll.title}</li>
          ))}
        </ul>
      )}

      {friendAvailability && friends.length > 0 && (
        <fieldset className="ml-6 space-y-1 border-l border-ink/25 pl-4">
          <legend className="text-sm font-medium text-ink-soft">
            {t('calendar.layers.friendFilterHeading')}
          </legend>
          {friends.map((friend) => (
            <label key={friend.ordinal} className="flex min-h-11 items-center gap-2">
              <input
                checked={shownFriendOrdinals.has(Number(friend.ordinal))}
                type="checkbox"
                onChange={(event) => toggleFriend(Number(friend.ordinal), event.target.checked)}
              />
              {friend.displayName ?? t('calendar.anonymousFriend')}
            </label>
          ))}
        </fieldset>
      )}
    </fieldset>
  )
}
