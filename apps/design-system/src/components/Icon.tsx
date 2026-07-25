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
