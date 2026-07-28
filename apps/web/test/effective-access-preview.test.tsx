import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { ThemeProvider } from "@mersal/design-system";
import { EffectiveAccessPreview, type EffectiveAccessKey } from "../src/screens/EffectiveAccessPreview";

/**
 * Phase 21.6 — the effective-access preview (design 40 §5/§6).
 *
 * The screen's job is not to list keys — it is to explain them. An administrator who sees a flat list
 * cannot tell a role grant from a hand-written exception, so they cannot review it, and a key that is
 * ABSENT because of a Deny override looks identical to one that was never granted.
 */
function renderPreview(keys: EffectiveAccessKey[]) {
  return render(
    <ThemeProvider>
      <EffectiveAccessPreview membershipId="m-1" keys={keys} />
    </ThemeProvider>,
  );
}

describe("Effective access preview (21.6)", () => {
  it("annotates every key with WHERE it came from", () => {
    renderPreview([
      { key: "finance:read", source: "role", via: "finance" },
      { key: "emr:read", source: "override", via: "admin-7", reason: "covering Alexandria in October" },
    ]);

    const rows = screen.getAllByRole("row").slice(1); // drop the header
    expect(rows).toHaveLength(2);

    const emr = rows.find((r) => within(r).queryByText("emr:read"))!;
    expect(emr).toHaveAttribute("data-source", "override");
    // The reason is the thing that makes an exception reviewable at all.
    expect(within(emr).getByText("covering Alexandria in October")).toBeInTheDocument();
  });

  it("shows a DENIED key rather than silently omitting it", () => {
    // The most useful line on the screen. Omitting it makes the absence look like a bug in the role
    // definition, and sends an administrator off to re-grant a role that was never the problem.
    renderPreview([
      { key: "orders:read", source: "denied", via: "admin-7", reason: "under investigation" },
    ]);

    const row = screen.getAllByRole("row")[1];
    expect(row).toHaveAttribute("data-source", "denied");
    expect(within(row).getByText("under investigation")).toBeInTheDocument();
  });

  it("renders a deprecated key muted with its replacement pointer", () => {
    // Deprecation is a migration signal, not enforcement — the key still works, so hiding it would leave
    // the administrator unable to see what they still have to move off.
    renderPreview([
      { key: "legacy:all", source: "role", via: "finance", deprecated: true, replacedBy: "orders:read" },
    ]);

    const row = screen.getAllByRole("row")[1];
    expect(row).toHaveAttribute("data-deprecated", "true");
    expect(within(row).getByText(/orders:read/)).toBeInTheDocument();
  });

  it("carries the source as TEXT, not colour alone", () => {
    // Four-cue status (21-accessibility): a chip whose only signal is hue is invisible to a large share of
    // users and to anyone printing the review.
    renderPreview([{ key: "finance:read", source: "role", via: "finance" }]);
    expect(screen.getByText("from role")).toBeInTheDocument();
  });

  it("says so plainly when a membership has no effective access", () => {
    renderPreview([]);
    expect(screen.getByText(/no effective access/i)).toBeInTheDocument();
  });

  it("is a table with a caption and column headers", () => {
    // The review is read by people using screen readers and by people printing it as evidence.
    renderPreview([{ key: "finance:read", source: "role" }]);
    expect(screen.getByRole("columnheader", { name: "Key" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Source" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Effective access" })).toBeInTheDocument();
  });
});
