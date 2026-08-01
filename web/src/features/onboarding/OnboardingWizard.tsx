import { useEffect, useReducer, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router'
import type { components } from '../../api/schema'
import { ApiError, client, isApiError, refreshAntiforgeryToken } from '../../api/client'
import { sessionQueryKey } from '../../auth/useSession'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'

type ProfileDraft = components['schemas']['ProfileDraft']
type AvatarDraft = components['schemas']['AvatarDraft']
const storageKey = 'partyup.onboarding-draft'
const steps = ['username', 'display-name', 'avatar', 'time-zone', 'link-platforms', 'friends'] as const
type Step = typeof steps[number]
function emptyDraft(): ProfileDraft { return { username: null, displayName: null, avatar: null, timeZone: null, locale: null } }
function loadDraft(): ProfileDraft { try { const saved: unknown = JSON.parse(sessionStorage.getItem(storageKey) ?? 'null'); return saved && typeof saved === 'object' ? { ...emptyDraft(), ...(saved as Partial<ProfileDraft>) } : emptyDraft() } catch { return emptyDraft() } }
function reducer(state: ProfileDraft, update: Partial<ProfileDraft>) { return { ...state, ...update } }
function fromPath(path: string): Step | undefined { return steps.find((step) => path.endsWith('/' + step)) }
function nextStep(draft: ProfileDraft): Step { if (!draft.username) return 'username'; if (!draft.displayName) return 'display-name'; if (!draft.avatar) return 'avatar'; if (!draft.timeZone) return 'time-zone'; return 'link-platforms' }
function errorText(error: unknown, t: (key: string, options?: object) => string) { return t(isApiError(error) ? error.code : 'generic', { defaultValue: t('generic') }) }
function availabilityError(reason: string | null | undefined) {
  switch (reason) {
    case 'taken': return 'username.taken'
    case 'reserved': return 'username.reserved'
    case 'cooldown': return 'username.cooldown'
    case 'profanity': return 'text.profanity'
    case 'invalid': return 'text.invalid'
    default: return 'generic'
  }
}

export function UsernameStep({ value, onNext }: { value: string | null; onNext: (username: string) => void }) {
  const { t } = useTranslation(['onboarding', 'errors']); const [username, setUsername] = useState(value ?? ''); const [error, setError] = useState<unknown>()
  async function submit(event: FormEvent) { event.preventDefault(); setError(undefined); try { const { data } = await client.GET('/api/v1/onboarding/username/availability', { params: { query: { username } } }); const availability = data; if (!availability?.available) { setError(new ApiError(400, availabilityError(availability?.reason))); return }; onNext(username) } catch (caught) { setError(caught) } }
  const message = error ? errorText(error, (key, options) => t(key, { ns: 'errors', ...options })) : null
  return <form onSubmit={submit}><Label htmlFor="username">{t('fields.username')}</Label><Input id="username" value={username} onChange={(event) => setUsername(event.target.value)} aria-describedby={message ? 'username-error' : undefined} required />{message && <p id="username-error" role="alert">{message}</p>}<Button type="submit">{t('actions.continue')}</Button></form>
}
function DisplayNameStep({ value, onNext }: { value: string | null; onNext: (displayName: string) => void }) { const { t } = useTranslation('onboarding'); const [displayName, setDisplayName] = useState(value ?? ''); return <form onSubmit={(event) => { event.preventDefault(); onNext(displayName) }}><Label htmlFor="display-name">{t('fields.displayName')}</Label><Input id="display-name" value={displayName} onChange={(event) => setDisplayName(event.target.value)} required /><Button type="submit">{t('actions.continue')}</Button></form> }
function AvatarStep({ value, onNext }: { value: AvatarDraft | null; onNext: (avatar: AvatarDraft) => void }) { const { t } = useTranslation('onboarding'); const [emoji, setEmoji] = useState(value?.emoji ?? 'game'); return <div role="radiogroup" aria-label={t('fields.avatar')}>{['game', 'dice', 'arcade'].map((option) => <button key={option} type="button" role="radio" aria-checked={emoji === option} onClick={() => setEmoji(option)}>{option}</button>)}<Button onClick={() => onNext({ kind: 'emoji', emoji, platformLinkId: null })}>{t('actions.continue')}</Button></div> }
function TimeZoneStep({ value, onNext }: { value: string | null; onNext: (timeZone: string) => void }) { const { t } = useTranslation('onboarding'); const [timeZone, setTimeZone] = useState(value ?? Intl.DateTimeFormat().resolvedOptions().timeZone); return <form onSubmit={(event) => { event.preventDefault(); onNext(timeZone) }}><Label htmlFor="time-zone">{t('fields.timeZone')}</Label><select id="time-zone" value={timeZone} onChange={(event) => setTimeZone(event.target.value)}><option value={timeZone}>{timeZone}</option><option value="UTC">UTC</option></select><Button type="submit">{t('actions.continue')}</Button></form> }

export function OnboardingWizard() {
  const { t, i18n } = useTranslation('onboarding'); const [platformsStepComplete, setPlatformsStepComplete] = useState(false); const navigate = useNavigate(); const location = useLocation(); const queryClient = useQueryClient(); const [draft, dispatch] = useReducer(reducer, undefined, loadDraft); const complete = useMutation({ mutationFn: (body: ProfileDraft) => client.POST('/api/v1/onboarding/complete', { params: { header: { 'X-CSRF-TOKEN': '' } }, body }), onSuccess: async () => { await refreshAntiforgeryToken(); await queryClient.invalidateQueries({ queryKey: sessionQueryKey }) } }); const heading = useRef<HTMLHeadingElement>(null)
  const requested = fromPath(location.pathname); const allowed = platformsStepComplete ? 'friends' : nextStep(draft); const requestedIndex = requested === undefined ? -1 : steps.indexOf(requested); const current = requested === undefined || requestedIndex > steps.indexOf(allowed) ? allowed : requested
  useEffect(() => { sessionStorage.setItem(storageKey, JSON.stringify(draft)) }, [draft])
  useEffect(() => { if (requested === undefined || requestedIndex > steps.indexOf(allowed)) navigate('/onboarding/' + allowed, { replace: true }) }, [allowed, navigate, requested, requestedIndex])
  useEffect(() => { heading.current?.focus() }, [current])
  function advance(update: Partial<ProfileDraft>) { const updated = { ...draft, ...update }; dispatch(update); navigate('/onboarding/' + nextStep(updated)) }
  async function finish() { if (complete.isPending) return; try { await complete.mutateAsync({ ...draft, locale: i18n.language }); sessionStorage.removeItem(storageKey); navigate('/app', { replace: true }) } catch { return } }
  const labels: Record<Step, string> = { username: 'pages.username', 'display-name': 'pages.displayName', avatar: 'pages.avatar', 'time-zone': 'pages.timeZone', 'link-platforms': 'pages.linkPlatforms', friends: 'pages.friends' }
  return <main data-testid="screen-onboarding"><p aria-live="polite">{t('progress', { current: steps.indexOf(current) + 1, total: steps.length, step: t(labels[current]) })}</p><h1 ref={heading} tabIndex={-1}>{t(labels[current])}</h1>{current === 'username' && <UsernameStep value={draft.username} onNext={(username) => advance({ username })} />}{current === 'display-name' && <DisplayNameStep value={draft.displayName} onNext={(displayName) => advance({ displayName })} />}{current === 'avatar' && <AvatarStep value={draft.avatar} onNext={(avatar) => advance({ avatar })} />}{current === 'time-zone' && <TimeZoneStep value={draft.timeZone} onNext={(timeZone) => advance({ timeZone })} />}{current === 'link-platforms' && <><p>{t('messages.platformLinksOptional')}</p><Button onClick={() => { setPlatformsStepComplete(true); navigate('/onboarding/friends') }}>{t('actions.continue')}</Button></>}{current === 'friends' && <><p>{t('messages.friendsPhaseThree')}</p><Button disabled={complete.isPending} onClick={() => void finish()}>{t('actions.finish')}</Button>{complete.error && <p role="alert">{errorText(complete.error, (key, options) => t(key, { ns: 'errors', ...options }))}</p>}</>}</main>
}
