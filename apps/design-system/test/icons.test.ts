import { describe, expect, it } from "vitest";
import { iconPaths } from "../src/components/Icon";

/**
 * One glyph, one meaning — asserted where the glyphs are defined.
 *
 * `Icon`'s own doc comment states the rule at length ("Each of these labels ONE thing and is never reused for
 * a second meaning on the same surface"), and the family had held to it. What it did NOT have was an upward
 * arrow, and the consequence followed: the two bulk-intake screens, whose entire purpose is putting a CSV on
 * the server, labelled their Upload button with `download` — an arrow pointing at the floor on the one control
 * that means "away from this machine".
 *
 * A missing glyph is therefore not a gap, it is a mislabelling waiting to happen, which is what these pin.
 */
describe("the icon family", () => {
  it("has both directions of the transfer pair", () => {
    expect(iconPaths).toHaveProperty("download");
    expect(iconPaths).toHaveProperty("upload");
  });

  it("draws them pointing opposite ways", () => {
    // The arrowhead is the middle path of each: `5 5 5-5` closes downward, `5-5 5 5` opens upward. Copy one
    // over the other and the pair silently becomes one glyph pointing one way.
    expect(iconPaths.download).toContain("m7 11 5 5 5-5");
    expect(iconPaths.upload).toContain("m7 10 5-5 5 5");
    expect(iconPaths.upload).not.toEqual(iconPaths.download);
  });

  it("shares the surface line, so only the direction of travel differs", () => {
    // Both sit on `M5 21h14`. Mirroring the whole glyph instead would put upload's line at the TOP, which
    // reads as a second arrow coming down out of something rather than one leaving the machine.
    expect(iconPaths.download).toContain('<path d="M5 21h14"/>');
    expect(iconPaths.upload).toContain('<path d="M5 21h14"/>');
  });

  it("defines every glyph exactly once", () => {
    // A duplicated path is two names for one drawing, which is the same failure as one name for two meanings.
    const byPath = new Map<string, string[]>();
    for (const [name, d] of Object.entries(iconPaths)) {
      byPath.set(d, [...(byPath.get(d) ?? []), name]);
    }
    const duplicates = [...byPath.values()].filter((names) => names.length > 1);
    expect(duplicates, "two icon names draw the same path — one of them is a synonym, not a glyph")
      .toEqual([]);
  });
});
