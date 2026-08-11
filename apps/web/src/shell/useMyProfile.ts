import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";

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
 * IT FAILS SOFT, DELIBERATELY
 * ============================================================================================================
 * The error state is swallowed and the hook returns null. This value decorates a button; a banner over the
 * whole application because a caption could not be fetched would be a worse outcome than the caption being
 * absent, and every caller already has a correct fallback for "no title recorded" because most accounts
 * genuinely have none.
 */
export function useMyProfile(): { displayName: string; position: string | null } | null {
  const api = useApi();
  const state = useAsync<{ displayName: string; position: string | null }>(() => api.myProfile(), []);
  return state.data;
}
