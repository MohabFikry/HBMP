import { useEffect, useState } from "react";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";

/**
 * The beneficiary's photograph, or their initials.
 *
 * ============================================================================================================
 * WHY THIS FETCHES INSTEAD OF SETTING src
 * ============================================================================================================
 * `GET /patients/{id}/photo` requires a bearer (profile-service, `RequireAuthorization`), and a bare
 * `<img src>` sends none. Behind the gateway that is a 401 the browser reports as a broken image — so the
 * avatar quietly fell back to initials for EVERY member, and the photo an officer had uploaded to help
 * identify somebody at the desk was never once displayed. Same defect as the bulk template download
 * (audit R3, dead-link #2), same fix: fetch the bytes WITH the token and hand the browser a blob.
 *
 * ============================================================================================================
 * NO PHOTO AND DECLINED LOOK IDENTICAL, DELIBERATELY
 * ============================================================================================================
 * The endpoint answers 404 both when none was taken and when the beneficiary refused photography, and this
 * renders initials for both. Distinguishing them would make a refusal visible to every user who opens the
 * record — which is its own disclosure, of exactly the kind a photograph of a refugee deserves protection
 * from. A 403 (the photo allow-list is narrower than the profile's) lands in the same place.
 *
 * Every successful retrieval is audited server-side as a disclosure of a person's face to a named user. That
 * is why this is not prefetched for a list: it is mounted where one record is open.
 */
export function MemberAvatar({
  beneficiaryId,
  name,
  size = 56,
}: {
  beneficiaryId: string;
  name: string;
  /** Pixel edge. The initials fallback scales with it, so one component serves the card and the strip. */
  size?: number;
}) {
  const [src, setSrc] = useState<string | null>(null);

  useEffect(() => {
    let objectUrl: string | null = null;
    let cancelled = false;

    void (async () => {
      try {
        const token = getToken();
        const res = await fetch(`${API_BASE}/patients/${beneficiaryId}/photo`, {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
        });
        if (!res.ok) return;                     // 404 no photo · 403 not your role · both → initials
        const blob = await res.blob();
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setSrc(objectUrl);
      } catch {
        // Offline or blocked. Initials are a complete answer, so there is nothing to report to the operator.
      }
    })();

    return () => {
      cancelled = true;
      // Revoked on unmount: a member lookup opens one record after another, and a blob per member held for
      // the life of the tab is a copy of every face the operator looked at, kept in the page.
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      setSrc(null);
    };
  }, [beneficiaryId]);

  const initials = name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0] ?? "")
    .join("")
    .toUpperCase();

  // aria-hidden on both: the name is rendered as text beside this, and a screen reader announcing it twice
  // adds nothing. An alt of "" on the img says the same thing for the photo.
  return src ? (
    <img
      className="mem-avatar"
      style={{ inlineSize: size, blockSize: size }}
      src={src}
      alt=""
      data-testid="member-photo"
    />
  ) : (
    <div
      className="mem-avatar mem-avatar--initials"
      style={{ inlineSize: size, blockSize: size, fontSize: Math.round(size / 2.6) }}
      aria-hidden="true"
      data-testid="member-initials"
    >
      {initials || "—"}
    </div>
  );
}
