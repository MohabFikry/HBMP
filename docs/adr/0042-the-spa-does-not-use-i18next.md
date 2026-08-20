# ADR-0042 — The SPA does not use i18next, and should not

**Status:** Accepted · **Date:** 2026-08-09 · **Supersedes:** the `i18next` line in root `CLAUDE.md`'s tech stack

## Context

`CLAUDE.md` names `i18next` in the baseline stack. `i18next` and `react-i18next` are dependencies of both
`apps/web` and `apps/design-system`. The **design-system gallery** genuinely uses them — `src/i18n/index.ts`,
`I18nextProvider` in `main.tsx`, `useTranslation` in `Gallery.tsx`.

The **portal application does not**, anywhere. Every operator-facing string in `apps/web` is a `Localized`
object — `{ en, ar }` — authored inline and rendered through `t()` from `useLoc()`, or held in the `L` table in
`src/i18n/strings.ts`.

The 2026-08-09 audit recorded this as drift and then, unusually, recommended against fixing it: *"i18next
declared but never imported (homegrown scheme is actually stronger — needs an ADR, not a 'fix')."* This is
that ADR.

## Decision

**The SPA keeps the `Localized` scheme. `CLAUDE.md`'s stack line is corrected to say so.** We do not migrate
the portals to `i18next`.

## Why the homegrown scheme is the better one *here*

**A missing translation cannot compile.** `Localized` is `{ en: string; ar: string }`, so a string authored in
one language is a type error at the point it is written. i18next resolves keys at runtime against a bundle: a
key with no `ar` entry falls back to English and renders, silently, to an Arabic-reading user. On this platform
that is not a cosmetic failure — it is a refusal reason, a dosage instruction or a consent prompt appearing in
a language the reader may not have. Roughly half the audit's i18n findings across three rounds have been
exactly this shape, and every one of them was a *runtime* discovery.

**The string is next to the thing it labels.** A reviewer reading a dispense refusal sees both languages in
the diff. With a key and two JSON bundles, the reviewer sees `t("pharmacy.refusal.expired")` and has to open
two other files to know what it says in either language — and a translation that drifts from its call site
drifts silently, because nothing references it.

**No key namespace to keep tidy.** There is no `pharmacy.refusal.expired` vs `pharmacy.errors.expiredLot` to
disagree about, no orphaned keys, and no way to typo a key into a blank screen.

**The platform has exactly two languages and both are first-class.** i18next earns its complexity with
plural-category machinery, locale negotiation, lazy per-locale bundle loading and translator workflows. Mersal
has English and Arabic, authored by the team, both shipped in every bundle. `Intl` already handles the number,
date and currency formatting that genuinely varies (`useFormat`), which is the part a library is actually
needed for.

## What we give up, stated honestly

- **No external translator workflow.** Handing `.json` bundles to a translation vendor is the thing i18next is
  best at, and we cannot do it. If Mersal ever translates into a third language with outside help, revisit
  this — the migration is mechanical (the `L` tables are already key→`{en,ar}` maps) and this ADR should be
  superseded rather than worked around.
- **No ICU plural categories.** Arabic has six. The app currently has no string that needs more than
  singular/plural, and the two places that come close spell both forms out. A seventh screen that needs real
  plural rules is a reason to reopen this, not a reason to hand-roll a plural engine.
- **Strings ship in every bundle, both languages.** Measured at roughly 40 kB, against a main chunk of 545 kB.
  Not worth a lazy-loading mechanism.

## Consequences

- `CLAUDE.md`'s stack line now reads that the design system uses `i18next` and the portals use the typed
  `Localized` scheme, with a pointer here. The two-sentence version of this decision lives where somebody will
  actually meet it.
- `i18next`/`react-i18next` stay as dependencies of **`apps/design-system`**, which uses them. They remain in
  `apps/web`'s manifest only because `test/render.tsx` wraps in `I18nextProvider` for design-system components
  rendered inside portal tests; that is a test-harness dependency, not an application one.
- Nobody should "finish the i18next migration". There is no migration in progress. This file is the answer to
  the next audit that finds a declared dependency with no import and reasonably asks why.
