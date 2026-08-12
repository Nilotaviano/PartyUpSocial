// Accepts a pasted Steam store URL (e.g.
// https://store.steampowered.com/app/730/Counter-Strike_2/) and extracts the
// numeric app id so users don't have to hand-copy it out of the URL.
const STEAM_STORE_URL_APP_ID = /store\.steampowered\.com\/app\/(\d+)/i

/**
 * Shared by SessionCreatePage, SessionEditPage, and PollCreatePage's Steam
 * app-id fields: a pasted store URL is reduced to its numeric app id, any
 * other input (including a hand-typed app id) passes through unchanged.
 */
export function extractSteamAppId(value: string): string {
  const urlMatch = value.match(STEAM_STORE_URL_APP_ID)
  return urlMatch ? urlMatch[1] : value
}
