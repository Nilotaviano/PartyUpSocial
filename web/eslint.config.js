import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
  },
  {
    // The two shadcn/ui primitives the calendar port carries keep their
    // upstream shape: `use-mobile` reads the viewport in an effect (there is
    // no render-safe source for it) and `button` exports its variant recipe
    // beside the component.
    files: [
      'src/features/availability/lovable/use-mobile.tsx',
      'src/features/availability/lovable/ui/button.tsx',
    ],
    rules: {
      'react-hooks/set-state-in-effect': 'off',
      'react-refresh/only-export-components': 'off',
    },
  },
  {
    // Mirrors openapi-typescript's own output shape for a spec with no paths
    // yet, so `npm run gen:api` can overwrite this placeholder without the
    // file shape changing.
    files: ['src/api/schema.d.ts'],
    rules: {
      '@typescript-eslint/no-empty-object-type': 'off',
    },
  },
])
