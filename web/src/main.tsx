import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router'
import { refreshAntiforgeryToken, registerSessionQueryClient } from './api/client'
import './i18n'
import '@fontsource-variable/gabarito/wght.css'
import '@fontsource-variable/nunito-sans/wght.css'
import './index.css'
import App from './App.tsx'
import { registerServiceWorker } from './features/pwa/registerServiceWorker'
import { watchSystemTheme } from './theme'

const queryClient = new QueryClient()
registerSessionQueryClient(queryClient)
void refreshAntiforgeryToken().catch(() => undefined)
watchSystemTheme()

// Dev is excluded from the automatic registration: a stale worker would shadow Vite's module
// graph. Dev serves '/sw.js' as an ES module, which this classic registration cannot parse --
// real registration paths (shell and push opt-in) need a production build.
if (import.meta.env.PROD) {
  registerServiceWorker()
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
