import { useEffect, useSyncExternalStore } from "react";
import { getToken } from "../auth/tokenStore";
import { GATEWAY_BASE } from "../config";
import { initials } from "./AppUserButton";

/**
 * A member of staff's photograph, or the initials that stand in for one — 28.15.
 *
 * ============================================================================================================
 * ONE STORE, AND THAT IS THE WHOLE POINT
 * ============================================================================================================
 * The first version let every `StaffAvatar` do its own fetch and hold its own blob. The app bar and the
 * account pane therefore had two independent copies of the same person's photograph, keyed by two independent
 * `bust` counters — so uploading a new picture updated the pane and left the BAR showing the old one, side by
 * side, on the same screen. Two sources of truth for one fact, disagreeing in the most visible place
 * possible.
 *
 * Everything now reads from the module-level map below and re-renders together through
 * `useSyncExternalStore`. `invalidateStaffPhoto` is the only way a photo changes, and when it fires every
 * avatar for that person changes at once because there is only one value for them to read.
 *
 * ============================================================================================================
 * WHY IT FETCHES INSTEAD OF SETTING src
 * ============================================================================================================
 * `GET /identity/users/{id}/photo` requires a bearer and a bare `<img src>` sends none — behind the gateway
 * that is a 401 the browser reports as a broken image. `MemberAvatar` learned this the expensive way for
 * beneficiary photos (audit R3, dead-link #2): every avatar silently fell back to initials and the picture
 * somebody had uploaded was never once displayed.
 *
 * ============================================================================================================
 * NOT `MemberAvatar`, AND THE DIFFERENCE IS NOT COSMETIC
 * ============================================================================================================
 * That component renders a REFUGEE's face: identity-sensitive, consent-gated, audited on every read, and
 * mounted only where one record is open so a list never prefetches a hundred of them. This one renders a
 * colleague's staff photo, which discloses what their display name already does. They look alike and are
 * governed differently, so they stay apart — a shared component would invite the looser rules to migrate
 * towards the stricter ones or, far worse, the other way.
 */

/** userId → object url, or null for "fetched, and there is no photo". */
const photos = new Map<string, string | null>();
/** Which ids have been asked about at all — `null` in `photos` means "no photo", not "not yet asked". */
const fetched = new Set<string>();
const inFlight = new Set<string>();
const listeners = new Set<() => void>();

function emit() {
  for (const l of listeners) l();
}

function subscribe(l: () => void) {
  listeners.add(l);
  return () => void listeners.delete(l);
}

/**
 * Forget somebody's photo so every avatar showing it re-reads.
 *
 * <p>Called after an upload or a removal. The response is `private, max-age=300`, so the refetch carries a
 * cache-buster: without one the browser would answer from its own cache and the person who just changed
 * their picture would keep seeing the old one for five minutes and conclude it had not worked.</p>
 */
export function invalidateStaffPhoto(userId: string): void {
  const old = photos.get(userId);
  if (old) URL.revokeObjectURL(old);
  photos.delete(userId);
  fetched.delete(userId);
  inFlight.delete(userId);
  emit();
}

/** Drop every cached face. Called on sign-out — the next person in this tab inherits nothing. */
export function clearStaffPhotos(): void {
  for (const url of photos.values()) if (url) URL.revokeObjectURL(url);
  photos.clear();
  fetched.clear();
  inFlight.clear();
  emit();
}

async function load(userId: string): Promise<void> {
  if (inFlight.has(userId)) return;
  inFlight.add(userId);
  try {
    const token = getToken();
    // The cache-buster is why an upload is visible immediately; see `invalidateStaffPhoto`.
    const res = await fetch(
      `${GATEWAY_BASE}/identity/users/${encodeURIComponent(userId)}/photo?v=${Date.now()}`,
      { headers: token ? { Authorization: `Bearer ${token}` } : {} },
    );
    // 404 is the ordinary answer for somebody who has not set one. Initials are a complete response, so
    // there is nothing to report to the operator here or on a network failure.
    photos.set(userId, res.ok ? URL.createObjectURL(await res.blob()) : null);
  } catch {
    photos.set(userId, null);
  } finally {
    fetched.add(userId);
    inFlight.delete(userId);
    emit();
  }
}

/**
 * The blob url for somebody's photo, or null when they have none (or it has not arrived yet).
 *
 * <p>Exported because the picker needs to know WHETHER there is a photo, to decide whether its control
 * offers "add" or "change". It reads the same store the avatar renders from, so the two cannot disagree.</p>
 */
export function useStaffPhoto(userId: string | undefined): string | null {
  const src = useSyncExternalStore(
    subscribe,
    () => (userId ? (photos.get(userId) ?? null) : null),
    () => null, // server snapshot; this never renders on a server, but the signature wants one
  );

  useEffect(() => {
    if (userId && !fetched.has(userId)) void load(userId);
  }, [userId, src]);

  return src;
}

export function StaffAvatar({
  userId,
  name,
  size = 56,
  className,
}: {
  userId: string | undefined;
  name: string;
  /** Pixel edge. The initials fallback scales with it, so one component serves the bar, the pane and a form. */
  size?: number;
  className?: string;
}) {
  const src = useStaffPhoto(userId);
  const style = { inlineSize: size, blockSize: size, fontSize: Math.round(size / 2.8) };

  // aria-hidden on both, and `alt=""` on the image: the person's name is rendered as text beside this
  // everywhere it is used, and announcing it twice adds nothing.
  return src ? (
    <img src={src} alt="" aria-hidden="true" className={className ?? "staff-avatar"} style={style} />
  ) : (
    <span className={className ?? "staff-avatar"} style={style} aria-hidden="true">
      {initials(name)}
    </span>
  );
}
