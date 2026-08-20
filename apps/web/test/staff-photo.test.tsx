import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PhotoPicker } from "../src/shell/PhotoPicker";
import { StaffAvatar, clearStaffPhotos, invalidateStaffPhoto } from "../src/shell/StaffAvatar";

/**
 * 28.15 follow-up — every avatar for one person shows the SAME picture, and shows it immediately.
 *
 * <p>Two defects lived here. The first shipped: each `StaffAvatar` fetched independently and held its own
 * blob, so the app bar and the account pane held two copies of one face and an upload updated only the one
 * that had asked for it. The second is what these tests exist to catch — a change that reaches the store but
 * never reaches the screen, so the picture is right only after a reload. Both are invisible in a unit test of
 * a single avatar, which is why these mount TWO.</p>
 */

const ONE = "11111111-1111-1111-1111-111111111111";

let served: string;
let fetches: number;
/** The server's state, so a DELETE actually removes the photo the next GET would return. */
let stored: boolean;

beforeEach(() => {
  served = "first";
  fetches = 0;
  stored = true;
  clearStaffPhotos();
  // A distinct object url per response, so "did the image change" is observable rather than inferred.
  globalThis.URL.createObjectURL = vi.fn(() => `blob:${served}`) as unknown as typeof URL.createObjectURL;
  globalThis.URL.revokeObjectURL = vi.fn();
  // Routed by METHOD, because a stub that answers every request the same way cannot tell a working
  // invalidation from a broken one — the first version of this file made DELETE look like it had failed.
  vi.stubGlobal("fetch", vi.fn(async (_url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    if (method === "DELETE") {
      stored = false;
      return { ok: true } as unknown as Response;
    }
    if (method === "PUT") {
      stored = true;
      return { ok: true } as unknown as Response;
    }
    fetches += 1;
    return stored
      ? ({ ok: true, blob: async () => new Blob(["x"]) } as unknown as Response)
      : ({ ok: false } as unknown as Response);
  }));
});

afterEach(() => {
  cleanup();
  clearStaffPhotos();
  vi.unstubAllGlobals();
});

describe("one face, one source of truth", () => {
  it("fetches ONCE however many avatars ask for the same person", async () => {
    render(
      <>
        <StaffAvatar userId={ONE} name="Org Admin" size={36} />
        <StaffAvatar userId={ONE} name="Org Admin" size={64} />
      </>,
    );

    await waitFor(() => expect(screen.getAllByRole("presentation", { hidden: true })).toHaveLength(2));
    // The app bar and the pane are the same request. Two would be wasteful; two INDEPENDENT ones are how the
    // two ended up showing different pictures in the first place.
    expect(fetches).toBe(1);
  });

  it("updates every avatar the moment the photo changes, with no remount", async () => {
    render(
      <>
        <StaffAvatar userId={ONE} name="Org Admin" size={36} />
        <StaffAvatar userId={ONE} name="Org Admin" size={64} />
      </>,
    );

    await waitFor(() => {
      const imgs = screen.getAllByRole("presentation", { hidden: true }) as HTMLImageElement[];
      expect(imgs.every((i) => i.getAttribute("src") === "blob:first")).toBe(true);
    });

    // What an upload does. THE ASSERTION THAT MATTERS: no reload, no remount, both change.
    served = "second";
    invalidateStaffPhoto(ONE);

    await waitFor(() => {
      const imgs = screen.getAllByRole("presentation", { hidden: true }) as HTMLImageElement[];
      expect(imgs).toHaveLength(2);
      expect(imgs.every((i) => i.getAttribute("src") === "blob:second")).toBe(true);
    });
  });

  it("falls back to initials when the person has no photo", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false }) as unknown as Response));
    render(<StaffAvatar userId={ONE} name="Org Admin" />);
    // 404 is ordinary. Initials are a complete answer, so there is no error state to render.
    expect(await screen.findByText("OA")).toBeInTheDocument();
  });
});

/**
 * The link the store tests do not reach: `PhotoPicker` → `invalidateStaffPhoto` → every avatar.
 *
 * <p>The store propagates correctly on its own, and each avatar renders correctly on its own. What is left is
 * whether the CONTROL is wired to the store at all — and a picker that writes to the server and forgets to
 * invalidate looks exactly like a working one until the page is reloaded.</p>
 */
describe("changing a photo reaches the screen without a reload", () => {
  it("drops back to initials the instant a photo is removed", async () => {
    const user = userEvent.setup();
    render(
      <>
        <StaffAvatar userId={ONE} name="Org Admin" size={36} />
        <PhotoPicker userId={ONE} name="Org Admin" variant="buttons" t={(l) => l.en} />
      </>,
    );

    // Both avatars start on the same photo, from one request.
    await waitFor(() => expect(screen.getAllByRole("presentation", { hidden: true })).toHaveLength(2));

    await user.click(screen.getByRole("button", { name: /remove/i }));

    // No reload, no remount: the picture is gone from BOTH the moment the server says so.
    await waitFor(() => {
      expect(screen.queryAllByRole("presentation", { hidden: true })).toHaveLength(0);
      expect(screen.getAllByText("OA")).toHaveLength(2);
    });
  });
});

describe("replacing a photo does not blank the avatar first", () => {
  it("keeps the current picture on screen until the new one arrives", async () => {
    // A slow GET, so the window between "invalidated" and "replacement in hand" is observable. Clearing the
    // store first would show initials for exactly that long — a flash that reads as the photo having been
    // deleted, immediately after somebody chose one.
    let release: (() => void) | null = null;
    const gate = new Promise<void>((r) => { release = r; });
    vi.stubGlobal("fetch", vi.fn(async (_u: string, init?: RequestInit) => {
      if ((init?.method ?? "GET") !== "GET") return { ok: true } as unknown as Response;
      if (fetches > 0) await gate;
      fetches += 1;
      return { ok: true, blob: async () => new Blob(["x"]) } as unknown as Response;
    }));

    render(<StaffAvatar userId={ONE} name="Org Admin" size={36} />);
    await waitFor(() => expect(screen.getByRole("presentation", { hidden: true })).toBeInTheDocument());

    served = "second";
    invalidateStaffPhoto(ONE);

    // Mid-flight: still the OLD picture, not initials.
    await waitFor(() => {
      const img = screen.getByRole("presentation", { hidden: true }) as HTMLImageElement;
      expect(img.getAttribute("src")).toBe("blob:first");
    });
    expect(screen.queryByText("OA")).toBeNull();

    release!();
    await waitFor(() => {
      const img = screen.getByRole("presentation", { hidden: true }) as HTMLImageElement;
      expect(img.getAttribute("src")).toBe("blob:second");
    });
  });
});
