/**
 * Phase 18.C1 (audit R2 W2) — the caller's active branch, as sent on every API request.
 *
 * Branch scoping was built end to end in Phase 14 — the ABAC condition, the permitted-branch resolver, the
 * `X-Active-Branch` contract, the switcher in the app bar — and then nothing ever sent the header. The
 * switcher changed a value in React state and posted `/me/active-branch`; the next data request went out
 * without it, so every branch-scoped worklist returned the caller's HOME branch regardless of what the UI
 * said was selected. Selecting a branch looked like it worked and changed nothing, which is worse than not
 * offering the control: a receptionist at the Giza desk could believe they were looking at the Giza queue.
 *
 * Kept in module memory rather than sessionStorage on purpose. The active branch is a per-session UI choice,
 * not a credential, and it is re-resolved from `/me/branches` on load — persisting it would let a stale id
 * from a previous session outlive a revoked assignment, and the server would then have to 403 a header the
 * user never knowingly set.
 *
 * The server NEVER trusts this header: a branch outside the caller's permitted set is a 403 and is audited
 * (design 37 §7). Sending it is how the caller expresses intent, not how the caller gains access.
 */
export const ACTIVE_BRANCH_HEADER = "X-Active-Branch";

let activeBranchId: string | null = null;

export function getActiveBranch(): string | null {
  return activeBranchId;
}

export function setActiveBranch(id: string | null): void {
  activeBranchId = id;
}

/** The header to merge into a request, or nothing when no branch is active (member-scoped roles, and every
 * request made before `/me/branches` has resolved). An absent header means "my default scope", which is the
 * behaviour every service already implements. */
export function activeBranchHeader(): Record<string, string> {
  return activeBranchId ? { [ACTIVE_BRANCH_HEADER]: activeBranchId } : {};
}
