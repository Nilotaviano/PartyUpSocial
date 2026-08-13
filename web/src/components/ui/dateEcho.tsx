import { useTranslation } from 'react-i18next'

/**
 * The locale echo beneath a native `datetime-local` (§3.1): the control keeps
 * its native picker (and its `mm/dd/yyyy` chrome), and the value it holds is
 * restated in the viewer's own locale so the field never reads as a foreign
 * format. Empty, it shows the format hint instead.
 */
export function DateEcho({ value }: { value: string }) {
  const { t, i18n } = useTranslation('common')
  const parsed = value ? new Date(value) : null
  const readable =
    parsed && !Number.isNaN(parsed.getTime())
      ? new Intl.DateTimeFormat(i18n.language, {
          dateStyle: 'full',
          timeStyle: 'short',
        }).format(parsed)
      : null

  return (
    <p className="font-body text-meta text-ink-soft">
      {readable ? t('form.dateEcho', { date: readable }) : t('form.dateHint')}
    </p>
  )
}
