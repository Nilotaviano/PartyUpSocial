import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui/button'
import { Input } from '../../components/ui/input'
import { Label } from '../../components/ui/label'
import {
  isWellFormedFriendCode,
  normalizeFriendCode,
} from '../../auth/friendContinuation'

type FriendCodeInputProps = {
  initialValue?: string
  onResolve: (friendCode: string) => void
}

export function FriendCodeInput({
  initialValue = '',
  onResolve,
}: FriendCodeInputProps) {
  const { t } = useTranslation(['friends', 'errors'])
  const [value, setValue] = useState(initialValue)
  const [invalid, setInvalid] = useState(false)

  function submit(event: FormEvent) {
    event.preventDefault()
    const normalized = normalizeFriendCode(value)
    if (!isWellFormedFriendCode(normalized)) {
      setInvalid(true)
      return
    }

    setInvalid(false)
    setValue(normalized)
    onResolve(normalized)
  }

  return (
    <form className="space-y-3" onSubmit={submit}>
      <Label htmlFor="friend-code">{t('fields.friendCode')}</Label>
      <div className="flex flex-wrap gap-2">
        <Input
          aria-describedby={
            invalid ? 'friend-code-help friend-code-error' : 'friend-code-help'
          }
          autoCapitalize="characters"
          autoComplete="off"
          className="min-w-56 flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono tracking-wider"
          id="friend-code"
          inputMode="text"
          maxLength={14}
          onChange={(event) => setValue(event.target.value)}
          spellCheck={false}
          value={value}
        />
        <Button
          className="rounded-lg bg-indigo-600 px-4 py-2 font-semibold text-white hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          type="submit"
        >
          {t('actions.resolve')}
        </Button>
      </div>
      <p className="text-sm text-slate-600" id="friend-code-help">
        {t('labels.friendCodeHelp')}
      </p>
      {invalid && (
        <p id="friend-code-error" role="alert">
          {t('friend_code_invalid', { ns: 'errors' })}
        </p>
      )}
    </form>
  )
}
