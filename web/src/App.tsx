import { useTranslation } from 'react-i18next'

function App() {
  const { t } = useTranslation()

  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-3 bg-white px-4 text-center dark:bg-slate-900">
      <h1 className="text-4xl font-bold tracking-tight text-slate-900 dark:text-white">
        {t('app.name')}
      </h1>
      <p className="text-lg text-slate-600 dark:text-slate-300">
        {t('app.tagline')}
      </p>
    </main>
  )
}

export default App
