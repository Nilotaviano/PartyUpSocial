# TODO / Backlog

Product-behavior items here need a PRODUCT_SPEC decision (new or amended D-number) before implementation (CLAUDE.md: propose spec changes before implementing them).

- **Auto-confirm suggested poll options**: when a participant suggests a poll option, automatically record their vote as Available ✅ on that option (today suggesting casts no vote; suggester must vote on their own option separately). Touches the poll voting domain (D-080 family) — needs a spec decision, then Domain (`Poll.SuggestOption`) + tests.
