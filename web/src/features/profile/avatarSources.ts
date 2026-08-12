import type { components } from '../../api/schema'

/**
 * AVATARS_DESIGN.md §3.5: the settings source chooser reads `MyProfileResponse.avatarSources`
 * and writes back a `{kind:'source', avatarSourceId}` `AvatarDraft`.
 */

export type AvatarSourceOption = components['schemas']['AvatarSourceOptionResponse']

export function avatarSourcesOf(
  profile: { avatarSources?: AvatarSourceOption[] } | null | undefined,
): AvatarSourceOption[] {
  return profile?.avatarSources ?? []
}

/** `AvatarSourceKind` (`ProfileEnums.cs`) serializes as PascalCase; the i18n key is camelCase. */
export function avatarSourceLabelKey(kind: string): string {
  return kind.length > 0 ? kind[0].toLowerCase() + kind.slice(1) : kind
}

export type AvatarSelection =
  | { kind: 'emoji'; emoji: string }
  | { kind: 'source'; avatarSourceId: string }

const SOURCE_PREFIX = 'source:'

export function encodeAvatarSelection(selection: AvatarSelection): string {
  return selection.kind === 'emoji' ? selection.emoji : `${SOURCE_PREFIX}${selection.avatarSourceId}`
}

export function decodeAvatarSelection(value: string): AvatarSelection {
  return value.startsWith(SOURCE_PREFIX)
    ? { kind: 'source', avatarSourceId: value.slice(SOURCE_PREFIX.length) }
    : { kind: 'emoji', emoji: value }
}

/** Reads the currently-stored avatar (`MyProfileResponse.avatar`) into the picker's selection encoding. */
export function selectionFromAvatar(
  avatar: components['schemas']['AvatarResponse'] | null | undefined,
  fallbackEmoji: string,
): string {
  if (avatar?.kind === 'external' && avatar.avatarSourceId) {
    return `${SOURCE_PREFIX}${avatar.avatarSourceId}`
  }
  return avatar?.emoji ?? fallbackEmoji
}

export type AvatarDraftBody = components['schemas']['AvatarDraft']

export function avatarDraftBody(selection: AvatarSelection): AvatarDraftBody {
  return selection.kind === 'emoji'
    ? { kind: 'emoji', emoji: selection.emoji, platformLinkId: null, avatarSourceId: null }
    : { kind: 'source', emoji: null, platformLinkId: null, avatarSourceId: selection.avatarSourceId }
}
