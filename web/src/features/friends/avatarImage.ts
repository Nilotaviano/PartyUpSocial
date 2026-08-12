/**
 * AVATARS_DESIGN.md §3.1: `AvatarResponse.imgUrl` is the proxy path for an external
 * (source-backed) avatar, never an upstream URL (D-191). The parameter is structural rather
 * than the generated `AvatarResponse` so call sites whose local avatar shape is narrower
 * (e.g. `SteamMatchedUserResponse`-derived view models) still type-check.
 */
export function avatarImgUrl(avatar: { imgUrl?: string | null } | null | undefined): string | null {
  return avatar?.imgUrl ?? null
}
