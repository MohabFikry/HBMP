import type { Localized } from "@mersal/contracts";
import { ApiError } from "./http";

/**
 * Phase 18.D1 (audit R2 U2) — ONE bilingual message for a failed mutation.
 *
 * Four screens caught every failure as `catch { setStatus("idle") }`: the spinner stopped, nothing appeared,
 * and the operator was left looking at a form that had apparently done nothing. The natural response is to
 * press the button again — and since none of those four sent an idempotency key, the second press created a
 * second clinical record.
 *
 * The rule this encodes: an operator must be able to tell, from the message alone, whether to RETRY, RELOAD,
 * or STOP. A 409 and a dropped connection look identical if both say "request failed", and they demand
 * opposite actions — retrying a 409 duplicates work, reloading after a network blip loses the form. So each
 * status maps to a sentence that names what happened and what to do next, and the server's own RFC-7807
 * `detail` is appended when it has one, because a service usually knows something the client does not.
 */
export interface WriteError {
  /** The message to render. Bilingual — every operator-facing string on this platform is (0B / 21). */
  message: Localized;
  /** What the operator should do. Screens use this to decide whether to keep the form's contents. */
  action: "retry" | "reload" | "reauth" | "stop";
  /** True when the operation may ALREADY have succeeded, so a blind retry is unsafe. */
  possiblyApplied: boolean;
}

const M = {
  network: {
    en: "Could not reach the server. Your work has not been saved — check the connection and try again.",
    ar: "تعذّر الوصول إلى الخادم. لم يتم حفظ عملك — تحقق من الاتصال وحاول مرة أخرى.",
  },
  unauthorized: {
    en: "Your session has ended. Sign in again to continue — nothing was saved.",
    ar: "انتهت جلستك. سجّل الدخول مرة أخرى للمتابعة — لم يتم حفظ أي شيء.",
  },
  forbidden: {
    en: "Your access has changed and no longer covers this action. Nothing was saved.",
    ar: "تغيّرت صلاحياتك ولم تعد تشمل هذا الإجراء. لم يتم حفظ أي شيء.",
  },
  notFound: {
    en: "This record no longer exists — it may have been cancelled or merged. Reload to see the current state.",
    ar: "لم يعد هذا السجل موجودًا — ربما أُلغي أو دُمج. أعد التحميل لعرض الحالة الحالية.",
  },
  conflict: {
    en: "This has already been actioned — someone else got there first. Reloading to show the current state.",
    ar: "تم تنفيذ هذا الإجراء بالفعل — سبقك إليه شخص آخر. جارٍ إعادة التحميل لعرض الحالة الحالية.",
  },
  precondition: {
    en: "This record changed since you opened it. Reloading — please review and re-apply your change.",
    ar: "تغيّر هذا السجل منذ فتحه. جارٍ إعادة التحميل — يرجى المراجعة وإعادة تطبيق التغيير.",
  },
  unprocessable: {
    en: "The details could not be accepted. Correct the highlighted fields and try again.",
    ar: "تعذّر قبول البيانات. صحّح الحقول المحددة وحاول مرة أخرى.",
  },
  rateLimited: {
    en: "Too many attempts in a short time. Wait a moment and try again.",
    ar: "محاولات كثيرة خلال وقت قصير. انتظر لحظة ثم حاول مرة أخرى.",
  },
  server: {
    // "May not have been" is the honest wording: the request reached the server and the outcome is unknown.
    // Telling an operator to retry a possibly-applied write is how duplicates get made.
    en: "The server could not complete this. It may not have been saved — reload before trying again.",
    ar: "تعذّر على الخادم إتمام العملية. قد لا يكون قد تم الحفظ — أعد التحميل قبل المحاولة مجددًا.",
  },
  schema: {
    // A contract mismatch is OUR defect, not the operator's. Say so, and do not invite a retry that will
    // fail identically.
    en: "The server's response did not match what this screen expects. This is a fault on our side — please report it.",
    ar: "لم تتطابق استجابة الخادم مع ما تتوقعه هذه الشاشة. هذا خلل لدينا — يرجى الإبلاغ عنه.",
  },
  unknown: {
    en: "Something went wrong. Reload the page to see the current state before trying again.",
    ar: "حدث خطأ ما. أعد تحميل الصفحة لعرض الحالة الحالية قبل المحاولة مجددًا.",
  },
} satisfies Record<string, Localized>;

/** Append the service's own `detail` — it is the only part that knows WHICH field or WHICH rule failed. */
function withDetail(base: Localized, detail?: string): Localized {
  if (!detail) return base;
  return { en: `${base.en} (${detail})`, ar: `${base.ar} (${detail})` };
}

export function writeErrorMessage(e: unknown): WriteError {
  if (!(e instanceof ApiError)) {
    return { message: M.unknown, action: "reload", possiblyApplied: true };
  }

  if (e.kind === "network") {
    // The request may never have left the machine, or may have been applied and the response lost. We cannot
    // tell — which is exactly why every write carries an idempotency key, making the retry safe.
    return { message: M.network, action: "retry", possiblyApplied: true };
  }
  if (e.kind === "schema") {
    return { message: M.schema, action: "stop", possiblyApplied: true };
  }

  const detail = e.problem?.detail ?? e.problem?.title;
  switch (e.status) {
    case 401: return { message: M.unauthorized, action: "reauth", possiblyApplied: false };
    case 403: return { message: withDetail(M.forbidden, detail), action: "stop", possiblyApplied: false };
    case 404: return { message: withDetail(M.notFound, detail), action: "reload", possiblyApplied: false };
    // 409 and 412 are the two the old catch-all hid most damagingly: both mean the world moved, and both
    // must NOT be retried blindly — but they are different moves and the operator needs to know which.
    case 409: return { message: withDetail(M.conflict, detail), action: "reload", possiblyApplied: true };
    case 412: return { message: withDetail(M.precondition, detail), action: "reload", possiblyApplied: false };
    case 422: return { message: withDetail(M.unprocessable, detail), action: "retry", possiblyApplied: false };
    case 429: return { message: M.rateLimited, action: "retry", possiblyApplied: false };
    default:
      if (e.status !== undefined && e.status >= 500)
        return { message: withDetail(M.server, detail), action: "reload", possiblyApplied: true };
      return { message: withDetail(M.unknown, detail), action: "reload", possiblyApplied: true };
  }
}
