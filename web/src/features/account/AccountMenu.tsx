import { useQuery } from '@tanstack/react-query'
import { LogOut, Settings, ShieldCheck, UserRound } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { useLogoutMutation } from '../../auth/useSession'
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '../../components/shell/sheet'
import { AvatarDisc } from '../friends/AvatarDisc'
import { avatarImgUrl } from '../friends/avatarImage'
import { getViewerProfile, viewerProfileKey } from '../sessions/sessionQueries'

const rowClasses =
  'flex h-12 w-full cursor-pointer items-center gap-3 rounded-xl border border-border bg-card px-3 text-left font-display text-[14px] font-bold hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus'

/**
 * D-183: the app chrome's trailing account menu. The trigger doubles as the D-182 tour's eighth
 * anchor (`data-tour="settings"`) — it must exist in the DOM regardless of the profile query's
 * state, so the tour anchor never disappears and the chrome never shifts while the profile loads.
 *
 * Rows are links inside the Sheet panel, so their accessible role is `link`, not `menuitem`.
 * Sign-out stays a plain button (it triggers an async mutation, not a navigation) and closes the
 * sheet itself before firing the request, so it cannot be fired twice.
 */
export function AccountMenu() {
  const { t } = useTranslation('common')
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const profile = useQuery({ queryKey: viewerProfileKey, queryFn: getViewerProfile })
  const logout = useLogoutMutation()

  function signOut() {
    if (logout.isPending) return
    setOpen(false)
    logout.mutate(undefined, {
      onSuccess: () => navigate('/welcome', { replace: true }),
    })
  }

  const name = profile.data?.displayName || profile.data?.username || t('accountMenu.trigger')
  const username = profile.data?.username

  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetTrigger asChild>
        <button
          aria-label={t('accountMenu.trigger')}
          className="grid size-11 shrink-0 cursor-pointer place-items-center rounded-xl hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          data-tour="settings"
          type="button"
        >
          <AvatarDisc
            emoji={profile.data?.avatar?.emoji}
            imgUrl={avatarImgUrl(profile.data?.avatar)}
            seed={profile.data?.userId ?? ''}
            size="xs"
          />
        </button>
      </SheetTrigger>
      <SheetContent closeLabel={t('accountMenu.closeLabel')}>
        <SheetHeader>
          <SheetTitle className="font-display text-base font-extrabold">{name}</SheetTitle>
          {username && <SheetDescription className="text-[13px] text-muted-foreground">@{username}</SheetDescription>}
        </SheetHeader>
        <div className="space-y-1.5 px-4 py-4">
          <SheetClose asChild>
            <Link className={rowClasses} to="/settings/profile">
              <UserRound aria-hidden className="size-4 shrink-0" />
              {t('accountMenu.profile')}
            </Link>
          </SheetClose>
          <SheetClose asChild>
            <Link className={rowClasses} to="/settings/account">
              <Settings aria-hidden className="size-4 shrink-0" />
              {t('navigation.settings')}
            </Link>
          </SheetClose>
          <SheetClose asChild>
            <Link className={rowClasses} to="/settings/notifications">
              <ShieldCheck aria-hidden className="size-4 shrink-0" />
              {t('accountMenu.privacy')}
            </Link>
          </SheetClose>
          <button
            className={rowClasses}
            disabled={logout.isPending}
            type="button"
            onClick={signOut}
          >
            <LogOut aria-hidden className="size-4 shrink-0" />
            {t('accountMenu.signOut')}
          </button>
        </div>
      </SheetContent>
    </Sheet>
  )
}
