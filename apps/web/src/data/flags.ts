/**
 * Country flags, as URLs to the vendored 4:3 SVGs in `src/assets/flags`.
 *
 * ============================================================================================================
 * WHY A GLOB RATHER THAN 97 IMPORTS
 * ============================================================================================================
 * A hand-written import list is a second place the country set has to be maintained, and the failure mode is
 * silent: add a nationality, forget the import, and the flag is simply missing for that one country. The glob
 * is resolved by Vite at BUILD time — it is not a runtime directory read — so adding an SVG beside its
 * neighbours is the whole change.
 *
 * `query: "?url"` hands each file to Vite's asset pipeline, which is what makes the size problem go away by
 * itself: the ~85 flags under 4 KB come back as `data:` URIs and cost no request, while the dozen with coats
 * of arms (ES is 80 KB on its own) become fingerprinted files served `immutable`. Inlining all of them would
 * have put a quarter of a megabyte into the main bundle for a decoration.
 */
const FILES = import.meta.glob<string>("../assets/flags/*.svg", { eager: true, query: "?url", import: "default" });

/** `{ sy: "data:image/svg+xml,…", es: "/assets/es-a1b2c3.svg", … }` */
const BY_CODE: Record<string, string> = Object.fromEntries(
  Object.entries(FILES).map(([path, url]) => [
    path.slice(path.lastIndexOf("/") + 1, -".svg".length).toLowerCase(),
    url,
  ]),
);

/**
 * The flag for an ISO 3166-1 alpha-2 code, or undefined when we do not carry one.
 *
 * <p>Undefined is an ordinary answer, not an error. A flag is decoration: every option that shows one is
 * also identified by name and searchable by name AND code, so a country with no asset renders as a label
 * with nothing beside it rather than as a gap that needs explaining.</p>
 */
export function flagUrl(code: string | null | undefined): string | undefined {
  if (!code || !/^[A-Za-z]{2}$/.test(code)) return undefined;
  return BY_CODE[code.toLowerCase()];
}
