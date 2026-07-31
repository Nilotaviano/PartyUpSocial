---
imports:
  - ./CLAUDE.md
---

# Universal Agent Profile (PartyUp)

This repository uses a strict, document-driven development process. All AI agents, coding assistants, and tools must ingest and strictly adhere to the engineering guidelines, build architectures, and command sets imported from `CLAUDE.md`.

## 🎯 Global Behavior Rules
- **Verify the Spec First:** Never write code or tests without cross-referencing the 4 authoritative documents defined in `CLAUDE.md`.
- **Zero Warnings Policy:** Every code modification must result in 0 compiler warnings (`TreatWarningsAsErrors` is active).
- **Localization & Accessibility:** All frontend UI additions must include translations in `en`, `pt-BR`, and `es`, and must comply with WCAG 2.2 AA standards from the first commit.
