import { useEffect, useState } from "react";
import { useApi } from "../api/ApiProvider";
import type { ApiClient } from "../api/client";

/**
 * The signed-in person's own display identity — their name and their POSITION (28.13).
 *
 * ============================================================================================================
 * WHY THIS IS A REQUEST AND NOT A CLAIM
 * ============================================================================================================
 * The obvious home for a job title is the token, beside `name`. It is the wrong home twice over.
 * `docs/security/token-contract.md` is frozen, and every claim in it is one that `libs/auth` or the SPA reads
 * to make a DECISION — a caption is not one, and adding it would ship a display string to the nineteen
 * services that validate a token. And a claim is a five-minute cache: correcting a typo in somebody's title
 * would not show until their access token expired, which for the person who just asked an administrator to
 * fix it is indistinguishable from the fix not working.
 *
 * ============================================================================================================
 * 28.14 — WHY IT CACHES, AND WHY IT REPORTS `loading`
 * ============================================================================================================
 * The first version returned `useAsync(...).data` and nothing else, which made the app bar flicker: `data` is
 * null while the request is in flight AND null when no title is recorded, so the caller's
 * `position ?? portal.eyebrow` fallback fired during the fetch. Every load showed the PORTAL's label for a
 * moment and then replaced it with the person's title — the one thing this line was changed to stop doing,
 * reintroduced as a flash. A value that is about to change must not be rendered at all; that is what
 * `loading` is for, and it is why this hook cannot just hand back `data`.
 *
 * Two more things made it worse, and both are fixed here rather than papered over:
 *
 *   * `useAsync` re-runs on every mount and additionally on every ACTIVE-BRANCH change (it subscribes to the
 *     branch store), so moving between the picker and a portal, or switching branch, re-fetched a value that
 *     depends on neither. The module-level cache below means N mounts share one request.
 *   * Nothing survived a reload, so the flash returned on every refresh. The sessionStorage mirror — the same
 *     device the access token uses, and per-tab for the same reason — lets a reload paint the right caption
 *     on the first frame, then revalidate quietly behind it.
 *
 * Keyed by SUBJECT. A cached caption belongs to the person it was fetched for, and signing in as somebody
 * else in the same tab must not inherit it.
 */

export interface MyProfile {
  displayName: string;
  position: string | null;
}

const KEY = "mersal-my-profile";

/** In-memory, so repeated mounts in one session neither refetch nor touch storage. */
let cache: { subject: string; profile: MyProfile } | null = null;
/** The one in-flight request, shared by every component that mounts while it is running. */
let inFlight: Promise<MyProfile | null> | null = null;

function readMirror(subject: string): MyProfile | null {
  try {
    const raw = sessionStorage.getItem(KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as { subject?: string; displayName?: string; position?: string | null };
    if (parsed.subject !== subject || typeof parsed.displayName !== "string") return null;
    return { displayName: parsed.displayName, position: parsed.position ?? null };
  } catch {
    return null; // corrupt or unavailable storage is a cache miss, never an error
  }
}

function writeMirror(subject: string, profile: MyProfile): void {
  try {
    sessionStorage.setItem(KEY, JSON.stringify({ subject, ...profile }));
  } catch {
    /* ignore — a caption is not worth failing over */
  }
}

/** Called on sign-out: the next person in this tab must not inherit the last one's caption. */
export function clearMyProfile(): void {
  cache = null;
  inFlight = null;
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    /* ignore */
  }
}

async function load(api: ApiClient, subject: string): Promise<MyProfile | null> {
  if (inFlight) return inFlight;
  inFlight = api
    .myProfile()
    .then((p) => {
      const profile: MyProfile = { displayName: p.displayName, position: p.position ?? null };
      cache = { subject, profile };
      writeMirror(subject, profile);
      return profile;
    })
    // FAILS SOFT. This value decorates a button; a banner over the whole application because a caption could
    // not be fetched would be worse than the caption being absent, and every caller has a correct fallback.
    // `loaded` still flips, so the fallback renders instead of the line staying blank for ever.
    .catch(() => null)
    .finally(() => {
      inFlight = null;
    });
  return inFlight;
}

/**
 * @param subject the signed-in account's id — the cache key. Pass `undefined` before a session exists.
 * @returns `profile` once known, and `loaded` saying whether the answer is final. **Render the fallback only
 *          when `loaded` is true**, or the flash this hook was rewritten to remove comes straight back.
 */
export function useMyProfile(subject: string | undefined): { profile: MyProfile | null; loaded: boolean } {
  const api = useApi();
  // Seeded synchronously from the caches so a reload has the right caption on its FIRST frame rather than
  // after a round trip. `useState`'s initialiser runs before paint; an effect would not.
  const [state, setState] = useState<{ profile: MyProfile | null; loaded: boolean }>(() => {
    if (!subject) return { profile: null, loaded: false };
    if (cache?.subject === subject) return { profile: cache.profile, loaded: true };
    const mirrored = readMirror(subject);
    return mirrored ? { profile: mirrored, loaded: true } : { profile: null, loaded: false };
  });

  useEffect(() => {
    if (!subject) return;
    let live = true;
    // Revalidates even on a cache hit — the mirror can be a session old, and an administrator who has just
    // corrected somebody's title should see it on their next page load rather than their next sign-in.
    void load(api, subject).then((p) => {
      // A FAILED revalidation keeps what we already had. Overwriting a good cached caption with null on a
      // transient blip would swap the person's title for the portal's label — the exact flicker this hook
      // exists to prevent, arriving by the back door instead of the front.
      if (live) setState((prev) => ({ profile: p ?? prev.profile, loaded: true }));
    });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subject]);

  return state;
}
