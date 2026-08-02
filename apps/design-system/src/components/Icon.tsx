import type { SVGProps } from "react";

/**
 * Outline icon family — 1.5px stroke, 24px grid, currentColor (0B §8b). Each status icon is drawn as a
 * distinct shape so it doubles as the color-blind shape cue. Decorative usages set aria-hidden.
 */
export const iconPaths = {
  ok: '<path d="M20 6 9 17l-5-5"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  half: '<circle cx="12" cy="12" r="9"/><path d="M12 3a9 9 0 0 1 0 18Z" fill="currentColor" stroke="none"/>',
  triangle: '<path d="M12 2 2 20h20L12 2Z"/><path d="M12 9v4M12 17h.01"/>',
  cross: '<path d="M18 6 6 18M6 6l12 12"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3-3"/>',
  doc: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  flask: '<path d="M9 3h6M10 3v6l-5 9a2 2 0 0 0 2 3h10a2 2 0 0 0 2-3l-5-9V3"/>',
  pill: '<path d="M10.5 20.5 4 14a4.9 4.9 0 0 1 7-7l6.5 6.5a4.9 4.9 0 0 1-7 7Z"/><path d="m8.5 8.5 7 7"/>',
  refer: '<path d="M7 17 17 7M7 7h10v10"/>',
  moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3 7 7 0 0 0 21 12.8Z"/>',
  user: '<path d="M20 21a8 8 0 0 0-16 0"/><circle cx="12" cy="7" r="4"/>',
  chart: '<path d="M3 3v18h18"/><path d="M7 14l4-4 3 3 5-6"/>',
  check2: '<path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/>',
  chevron: '<path d="m6 9 6 6 6-6"/>',
  bell: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.7 21a2 2 0 0 1-3.4 0"/>',
  /** Branch / clinic site — a building with a cross. Replaces the 🏥 emoji the switcher used: an emoji
   *  renders in the platform font, ignores currentColor and sits off the icon baseline. */
  branch: '<path d="M4 21V7l8-4 8 4v14"/><path d="M9 21v-6h6v6"/><path d="M12 8v3M10.5 9.5h3"/>',
  /** View — opens a document in place. Paired with `download` below, the two make the distinction the audit
   *  trail draws: looking at a record and taking a copy of it are different disclosures. */
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/>',
  download: '<path d="M12 3v12"/><path d="m7 11 5 5 5-5"/><path d="M5 21h14"/>',
  /** Edit — a pen over its underline. `doc` was standing in for this and reads as "open a document", which is
   *  what the note affordance beside it actually does; two different actions must not share a glyph. */
  pen: '<path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z"/>',

  // ---- The member card (19.6d) -------------------------------------------------------------------------
  // Each of these labels ONE thing and is never reused for a second meaning on the same surface. A glyph that
  // means "a person" beside a button and "the person's sex" two lines above it teaches an operator that the
  // icons carry nothing, which is worse than having none.

  /** More than one person — the covered household. Distinct from `user`, which opens ONE person's file. */
  users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  /** A date, and by extension an age. */
  calendar: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>',
  /** Sex. The combined venus/mars mark rather than a second person glyph. */
  sex: '<circle cx="11" cy="13" r="4"/><path d="m14 10 5-5M15 5h4v4"/><path d="M11 17v4M9 19h4"/>',
  /** Nationality — a globe, not a flag: there is one glyph for the field, and flags would need 200. */
  globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a15 15 0 0 1 0 18 15 15 0 0 1 0-18Z"/>',
  phone: '<path d="M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2Z"/>',
  /** Move between two things — a plan change, where the member goes one way and nothing comes back. */
  swap: '<path d="M4 8h13"/><path d="m13 4 4 4-4 4"/><path d="M20 16H7"/><path d="m11 12-4 4 4 4"/>',
  /** A container something is filed into — the member group. */
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/>',
  /** A state that can be moved between positions — the beneficiary's status. */
  toggle: '<rect x="2" y="7" width="20" height="10" rx="5"/><circle cx="16" cy="12" r="3"/>',
  /** Put back what was ended. */
  undo: '<path d="M3 8h10a5 5 0 0 1 0 10H8"/><path d="m7 4-4 4 4 4"/>',
  /**
   * Withheld pending a justified request — the SHAPE cue on a restricted clinical result (design 37 §6).
   *
   * The restricted card drew this as a 🔒 emoji. An emoji is not a design-system icon: it is painted by the
   * platform's own font, so it differs between Windows, macOS and Android, ignores `currentColor` entirely,
   * and stays full-colour in a theme it was never designed for. On the one card that has to read as
   * unmistakably different from an ordinary result, "looks different on every machine" is the wrong property.
   */
  lock: '<rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',

  // ---- Vital signs -------------------------------------------------------------------------------------
  // One glyph per measurement, and none of them borrowed from the status family above.
  //
  // The encounter workspace first labelled these rows with `chart`, `half`, `triangle` and `info` — three of
  // which are STATUS shapes. `triangle` was the worst: it labelled "Temperature" on the left of the row and
  // simultaneously meant "out of range" in the flag chip on the right of the same row, so one glyph carried
  // two meanings eighteen pixels apart. That is exactly what the member-card note above warns against.

  /** Blood pressure — a dial with a needle, the sphygmomanometer's face. */
  gauge: '<circle cx="12" cy="12" r="2"/><path d="m13.4 10.6 3.6-3.6"/><path d="M4.2 18a9 9 0 1 1 15.6 0"/>',
  /** Heart rate. */
  heart: '<path d="M12 20.3 4.3 12.9a4.8 4.8 0 0 1 6.8-6.8l.9.9.9-.9a4.8 4.8 0 0 1 6.8 6.8Z"/>',
  temperature: '<path d="M14 14.8V5a2 2 0 1 0-4 0v9.8a4 4 0 1 0 4 0Z"/><path d="M12 9.5v6"/>',
  /** Oxygen saturation — the drop the pulse oximeter reads through. */
  droplet: '<path d="M12 3.2s6 6.3 6 10.1a6 6 0 0 1-12 0c0-3.8 6-10.1 6-10.1Z"/>',
  /** Height — a ruler with its graduations. */
  ruler: '<rect x="2" y="7" width="20" height="10" rx="2"/><path d="M7 7v3M12 7v4M17 7v3"/>',
  /** Weight — a balance, not a bathroom scale: the dial of one is indistinguishable from `gauge` above. */
  scale: '<path d="M12 4v17"/><path d="M7 21h10"/><path d="M4 7h16"/><path d="M4 7 1.5 13a3 3 0 0 0 5 0Z"/><path d="M20 7l-2.5 6a3 3 0 0 0 5 0Z"/>',
} as const;

export type IconName = keyof typeof iconPaths;

export interface IconProps extends SVGProps<SVGSVGElement> {
  name: IconName;
  /** Decorative icons should stay aria-hidden (default). Set a title for meaningful standalone icons. */
  title?: string;
}

export function Icon({ name, title, ...rest }: IconProps) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden={title ? undefined : true}
      role={title ? "img" : undefined}
      {...rest}
      dangerouslySetInnerHTML={{ __html: (title ? `<title>${title}</title>` : "") + iconPaths[name] }}
    />
  );
}
