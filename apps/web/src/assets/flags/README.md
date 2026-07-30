# Country flags

97 SVG flags, one per ISO 3166-1 alpha-2 code in `src/data/nationalities.ts`, at 4:3.

## Where they came from

Vendored from [`flag-icons`](https://github.com/lipis/flag-icons) v7.5.0, MIT — see `LICENSE` beside this
file. Only the codes the nationality list actually offers were copied; the upstream package carries 271.

## Why vendored rather than a dependency

`flag-icons` ships a CSS framework (background-image classes over a sprite) that we do not use, and a
runtime dependency for what are static assets buys nothing. Copying the 97 files we need keeps the platform's
offline-first posture — nothing here resolves at runtime — and means `pnpm install` cannot change what a
flag looks like.

## Why not emoji

Regional-indicator emoji (🇸🇾) were the first attempt: zero bytes, derived arithmetically from the code, no
assets to maintain. Windows ships no flag glyphs, so they rendered there as the two letters — legible, but
the field lost the thing it was added for. These render everywhere.

## How they are loaded

Through Vite's `import.meta.glob(..., { query: "?url" })` in `src/data/flags.ts`, so:

- the ~85 flags under 4 KB are inlined as `data:` URIs and cost no request;
- the 12 larger ones (coats of arms — ES, OM, AF, IR, LK …) become fingerprinted files served with
  `Cache-Control: immutable`;
- a code with no asset simply renders nothing, because a flag is decoration and every option is identified
  by its name.

## Updating

Add the country to `nationalities.ts`, then copy `flags/4x3/<code>.svg` from the upstream package. Nothing
else needs changing — the glob picks it up.
