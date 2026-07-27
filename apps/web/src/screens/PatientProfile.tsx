import { useCallback, useMemo, useRef, useState } from "react";
import { Button, Card, InlineAlert, useTheme } from "@mersal/design-system";
import type {
  CallHistoryRow,
  CallHistorySection,
  Localized,
  PatientProfile as PatientProfileContract,
  ProfileAlerts,
  ProfileHeader,
  ProfileSection,
} from "@mersal/contracts";
import { PROFILE_SECTION_KEYS } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";

/**
 * Phase 20.4 — the unified patient profile (design 39 §6).
 *
 * <b>ONE component, role-driven — not one screen per role.</b> It renders whatever sections the API returned,
 * in the order it returned them, and contains no role logic beyond rendering states. That is not a stylistic
 * preference: the moment this file asks "is the user a doctor?", the server's decision and the screen's
 * decision are two decisions, and the second one is the one an attacker gets to influence.
 *
 * The three non-visible states are rendered as three visibly and semantically different things. A user must
 * never confuse "you may not see this", "it broke", and "there is nothing here" — the first is a permissions
 * question, the second is a retry, and the third is a clinical fact. Collapsing them is how a clinician comes
 * to believe a patient has no allergies recorded.
 */

const STR = {
  title: { en: "Patient profile", ar: "ملف المريض" },
  jumpTo: { en: "Jump to section", ar: "الانتقال إلى قسم" },
  restricted: { en: "Restricted", ar: "مقيّد" },
  unavailable: { en: "Temporarily unavailable", ar: "غير متاح مؤقتًا" },
  empty: { en: "No records", ar: "لا توجد سجلات" },
  retry: { en: "Retry", ar: "إعادة المحاولة" },
  requestAccess: { en: "Request access", ar: "طلب الوصول" },
  copy: { en: "Copy summary", ar: "نسخ الملخص" },
  copyAll: { en: "Copy all visible", ar: "نسخ كل المعروض" },
  copied: { en: "Call summary copied", ar: "تم نسخ ملخص المكالمة" },
  copiedAll: { en: "Call summaries copied", ar: "تم نسخ ملخصات المكالمات" },
  copyFallback: {
    en: "Copying is unavailable on this connection. Select the text below and copy it.",
    ar: "النسخ غير متاح على هذا الاتصال. حدّد النص أدناه وانسخه.",
  },
  close: { en: "Close", ar: "إغلاق" },
  noSummaryAtLevel: {
    en: "Summary not available at your access level",
    ar: "الملخص غير متاح لمستوى وصولك",
  },
  edited: { en: "edited", ar: "معدّل" },
  inbound: { en: "Inbound", ar: "وارد" },
  outbound: { en: "Outbound", ar: "صادر" },
  alerts: { en: "Alerts", ar: "تنبيهات" },
} satisfies Record<string, Localized>;

/** Bilingual section titles, keyed by the server's section keys (design 39 §3 order). */
const SECTION_TITLES: Record<string, Localized> = {
  header: { en: "Identity", ar: "الهوية" },
  alerts: { en: "Alerts", ar: "التنبيهات" },
  coverage: { en: "Coverage & eligibility", ar: "التغطية والأهلية" },
  pastMedicalHistory: { en: "Past medical history", ar: "التاريخ المرضي" },
  encounters: { en: "Encounters", ar: "الزيارات" },
  investigations: { en: "Investigations & results", ar: "الفحوصات والنتائج" },
  prescriptions: { en: "Prescriptions & dispensing", ar: "الوصفات والصرف" },
  authorizations: { en: "Authorizations", ar: "الموافقات" },
  referrals: { en: "Referrals", ar: "الإحالات" },
  documents: { en: "Documents", ar: "المستندات" },
  notes: { en: "Notes", ar: "الملاحظات" },
  financial: { en: "Financial", ar: "المالية" },
  caseManagement: { en: "Case management", ar: "إدارة الحالة" },
  timeline: { en: "Timeline", ar: "السجل الزمني" },
  callHistory: { en: "Call history", ar: "سجل المكالمات" },
};

/** Why a section was withheld, in words. A reason code rendered raw is a reason nobody acts on. */
const REASONS: Record<string, Localized> = {
  "not-treating": {
    en: "You are not currently recorded as treating this patient.",
    ar: "لست مسجّلاً حاليًا كمعالج لهذا المريض.",
  },
  "not-assigned": {
    en: "This section is available to the case manager assigned to this beneficiary.",
    ar: "هذا القسم متاح لمدير الحالة المكلّف بهذا المستفيد.",
  },
  "role-not-permitted": {
    en: "Your role does not include this section.",
    ar: "دورك لا يشمل هذا القسم.",
  },
  "sensitive-requires-grant": {
    en: "This result is sensitivity-restricted. It stays existence-only until access is granted.",
    ar: "هذه النتيجة مقيّدة الحساسية. تبقى معلومة الوجود فقط حتى يُمنح الوصول.",
  },
};

export function PatientProfile({ beneficiaryId }: { beneficiaryId?: string }) {
  const api = useApi();
  const t = useLoc();
  const id = beneficiaryId ?? "BEN-2";
  const state = useAsync(useCallback(() => api.patientProfile(id), [api, id]), [id]);

  return (
    <>
      <PageHeader title={t(STR.title)} />
      <AsyncSection state={state} emptyLabel={STR.empty} isEmpty={(p) => p.sections.length === 0}>
        {(profile) => <ProfileBody profile={profile} onRetry={state.reload} />}
      </AsyncSection>
    </>
  );
}

function ProfileBody({ profile, onRetry }: { profile: PatientProfileContract; onRetry: () => void }) {
  const t = useLoc();

  // Render in the server's order, which is design 39 §3 order. Sorting here would be a second opinion about
  // the order alerts appear in, and alerts being second is a safety property, not a layout choice.
  const ordered = useMemo(() => {
    const rank = new Map(PROFILE_SECTION_KEYS.map((k, i) => [k as string, i]));
    return [...profile.sections].sort(
      (a, b) => (rank.get(a.key) ?? 999) - (rank.get(b.key) ?? 999),
    );
  }, [profile.sections]);

  return (
    <div className="patient-profile">
      <nav className="profile-jump" aria-label={t(STR.jumpTo)}>
        <ul>
          {ordered.map((s) => (
            <li key={s.key}>
              <a href={`#section-${s.key}`}>{t(SECTION_TITLES[s.key] ?? { en: s.key, ar: s.key })}</a>
            </li>
          ))}
        </ul>
      </nav>

      <div className="profile-sections">
        {ordered.map((section) => (
          <SectionCard
            key={section.key}
            section={section}
            beneficiaryId={profile.beneficiaryId}
            onRetry={onRetry}
          />
        ))}
      </div>
    </div>
  );
}

function SectionCard({
  section,
  beneficiaryId,
  onRetry,
}: {
  section: ProfileSection;
  beneficiaryId: string;
  onRetry: () => void;
}) {
  const t = useLoc();
  const title = t(SECTION_TITLES[section.key] ?? { en: section.key, ar: section.key });

  return (
    <section id={`section-${section.key}`} aria-labelledby={`h-${section.key}`} className="profile-section">
      <Card style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
        <h2 id={`h-${section.key}`} style={{ margin: 0, fontSize: "1.05rem" }}>
          {title}
        </h2>
        <SectionState section={section} beneficiaryId={beneficiaryId} onRetry={onRetry} />
      </Card>
    </section>
  );
}

/**
 * The three-state renderer. This is a correctness requirement rather than polish (build prompt 20.4), so each
 * state gets its own treatment AND its own words:
 *
 *  - Restricted  — a locked card with FOUR cues (neutral hue + lock icon + ghost pill + the word "Restricted"),
 *                  the reason in a sentence, and the request-access action where one is actually offered.
 *  - Unavailable — a warning treatment with Retry. Something broke; the record is unknown, not empty.
 *  - Empty       — plain "No records". A calm, ordinary fact.
 */
function SectionState({
  section,
  beneficiaryId,
  onRetry,
}: {
  section: ProfileSection;
  beneficiaryId: string;
  onRetry: () => void;
}) {
  const t = useLoc();

  if (section.state === "Restricted") {
    const reason = REASONS[section.reasonCode ?? "role-not-permitted"] ?? REASONS["role-not-permitted"];
    return (
      <div className="profile-restricted" data-state="restricted">
        <p className="profile-chip profile-chip--restricted">
          <span aria-hidden="true" className="profile-chip-icon">
            🔒
          </span>
          <span>{t(STR.restricted)}</span>
        </p>
        <p>{t(reason)}</p>
        {section.requestAccessAction ? (
          <Button
            variant="secondary"
            onClick={() => {
              window.location.assign(
                `/clinician/result-access?beneficiaryId=${encodeURIComponent(beneficiaryId)}`,
              );
            }}
          >
            {section.requestAccessAction.label ?? t(STR.requestAccess)}
          </Button>
        ) : null}
      </div>
    );
  }

  if (section.state === "Unavailable") {
    return (
      <div data-state="unavailable" style={{ display: "grid", gap: "var(--sp3)" }}>
        <InlineAlert tone="bad">
          <span>{t(STR.unavailable)}</span>
          {section.reasonCode ? (
            <span style={{ display: "block", marginTop: "var(--sp1)", opacity: 0.85, fontSize: "0.9em" }}>
              {section.reasonCode}
            </span>
          ) : null}
        </InlineAlert>
        <div>
          <Button variant="secondary" onClick={onRetry}>
            {t(STR.retry)}
          </Button>
        </div>
      </div>
    );
  }

  if (section.state === "NotApplicable") {
    return (
      <p data-state="empty" className="profile-empty">
        {t(STR.empty)}
      </p>
    );
  }

  return <SectionContent section={section} beneficiaryId={beneficiaryId} />;
}

/** Visible content. Call history has a bespoke renderer (design 39 §5b); everything else is a generic view. */
function SectionContent({ section, beneficiaryId }: { section: ProfileSection; beneficiaryId: string }) {
  if (section.key === "header") return <HeaderView data={section.data as ProfileHeader} />;
  if (section.key === "alerts") return <AlertsView data={section.data as ProfileAlerts} />;
  if (section.key === "callHistory") {
    return <CallHistoryView data={section.data as CallHistorySection} beneficiaryId={beneficiaryId} />;
  }
  return <GenericView data={section.data} />;
}

function HeaderView({ data }: { data: ProfileHeader }) {
  const { lang } = useTheme();
  const name = lang === "ar" && data.displayNameAr ? data.displayNameAr : data.displayName;
  return (
    <div className="profile-header-strip">
      <Avatar photoUrl={data.photoUrl} name={name} />
      <div>
        <p className="profile-name">{name}</p>
        <p className="profile-meta">
          {[data.memberNo, data.ageBand, data.sex, data.branchName].filter(Boolean).join(" · ")}
        </p>
        {/* Four cues: the tone, the icon, the shape and the word — never colour alone. */}
        <p className={`profile-chip profile-chip--${data.statusCue.tone}`} data-shape={data.statusCue.shape}>
          <span aria-hidden="true" className="profile-chip-icon">
            {data.statusCue.icon === "check-circle" ? "✔" : "●"}
          </span>
          <span>{data.statusCue.label}</span>
        </p>
      </div>
    </div>
  );
}

/**
 * An initials avatar when no photo came back — which happens both when none was taken and when the beneficiary
 * DECLINED. The two look identical here on purpose: a UI that distinguishes them would make a refusal visible
 * to every user who opens the record, which is its own disclosure.
 */
function Avatar({ photoUrl, name }: { photoUrl?: string; name: string }) {
  const [failed, setFailed] = useState(false);
  const initials = name
    .split(/\s+/)
    .slice(0, 2)
    .map((p) => p[0] ?? "")
    .join("")
    .toUpperCase();

  if (!photoUrl || failed) {
    return (
      <div className="profile-avatar profile-avatar--initials" aria-hidden="true">
        {initials}
      </div>
    );
  }
  return (
    <img
      className="profile-avatar"
      src={photoUrl}
      alt=""
      onError={() => setFailed(true)}
    />
  );
}

function AlertsView({ data }: { data: ProfileAlerts }) {
  const t = useLoc();
  const flags = [...(data.criticalFlags ?? []), ...(data.interactionWarnings ?? []), ...(data.operationalFlags ?? [])];
  if (data.allergies.length === 0 && flags.length === 0) {
    return <p className="profile-empty">{t(STR.empty)}</p>;
  }
  return (
    <ul className="profile-alerts">
      {data.allergies.map((a) => (
        <li key={a.allergen} className="profile-chip profile-chip--critical" data-shape="octagon">
          <span aria-hidden="true" className="profile-chip-icon">
            ⚠
          </span>
          <span>
            {a.allergen}
            {a.reaction ? ` — ${a.reaction}` : ""} ({a.severity})
          </span>
        </li>
      ))}
      {flags.map((f) => (
        <li key={f.label} className={`profile-chip profile-chip--${f.tone}`}>
          <span aria-hidden="true" className="profile-chip-icon">
            ⚑
          </span>
          <span>{f.label}</span>
        </li>
      ))}
    </ul>
  );
}

/** A readable dump of a section payload the profile does not yet render bespoke. Never a JSON blob. */
function GenericView({ data }: { data: unknown }) {
  const t = useLoc();
  if (data === null || data === undefined) return <p className="profile-empty">{t(STR.empty)}</p>;

  const rows = Array.isArray((data as { items?: unknown }).items)
    ? ((data as { items: Record<string, unknown>[] }).items)
    : null;

  if (rows) {
    if (rows.length === 0) return <p className="profile-empty">{t(STR.empty)}</p>;
    return (
      <ul className="profile-rows">
        {rows.map((row, i) => (
          <li key={String(row.orderRef ?? row.rxRef ?? row.authNo ?? row.encounterRef ?? i)}>
            {Object.entries(row)
              .filter(([, v]) => v !== null && v !== undefined && typeof v !== "object")
              .map(([k, v]) => `${k}: ${String(v)}`)
              .join(" · ")}
          </li>
        ))}
      </ul>
    );
  }

  const entries = Object.entries(data as Record<string, unknown>).filter(
    ([, v]) => v !== null && v !== undefined && typeof v !== "object",
  );
  if (entries.length === 0) return <p className="profile-empty">{t(STR.empty)}</p>;
  return (
    <dl className="profile-facts">
      {entries.map(([k, v]) => (
        <div key={k}>
          <dt>{k}</dt>
          <dd>{String(v)}</dd>
        </div>
      ))}
    </dl>
  );
}

// ---------------------------------------------------------------- call history (design 39 §5b)

/**
 * Direction, with FOUR cues: hue AND arrow icon AND chip shape AND the word. Never colour alone (0B).
 *
 * The arrows mirror in RTL — an inbound call arrives from the direction reading starts, so a hard-coded ↙
 * points the wrong way in Arabic. The WORD is translated, not transliterated.
 */
function DirectionChip({ direction }: { direction: "Inbound" | "Outbound" }) {
  const t = useLoc();
  const { lang } = useTheme();
  const rtl = lang === "ar";
  const inbound = direction === "Inbound";
  const icon = inbound ? (rtl ? "↘" : "↙") : rtl ? "↖" : "↗";

  return (
    <span
      className={`profile-chip profile-chip--${inbound ? "inbound" : "outbound"}`}
      data-shape={inbound ? "circle" : "square"}
      data-direction={direction}
    >
      <span aria-hidden="true" className="profile-chip-icon">
        {icon}
      </span>
      <span>{t(inbound ? STR.inbound : STR.outbound)}</span>
    </span>
  );
}

function CallHistoryView({ data, beneficiaryId }: { data: CallHistorySection; beneficiaryId: string }) {
  const t = useLoc();
  const api = useApi();
  const [announcement, setAnnouncement] = useState("");
  const [fallbackText, setFallbackText] = useState<string | null>(null);
  const [direction, setDirection] = useState<"all" | "Inbound" | "Outbound">("all");
  const fallbackRef = useRef<HTMLTextAreaElement>(null);

  /**
   * Put a SERVER-PROVIDED string on the clipboard. Never a client-assembled one, never `innerText` scraped
   * from the DOM: the block was generated from the projection this caller was actually served, so a field the
   * projection dropped cannot be in it.
   *
   * `navigator.clipboard` is unavailable on http origins and in some embedded browsers. Falling back to a
   * selectable textarea beats failing silently — a copy button that does nothing teaches users to screenshot.
   */
  const copy = useCallback(
    async (text: string, announce: Localized) => {
      try {
        if (!navigator.clipboard?.writeText) throw new Error("clipboard-unavailable");
        await navigator.clipboard.writeText(text);
        setAnnouncement(t(announce));
      } catch {
        setFallbackText(text);
        queueMicrotask(() => fallbackRef.current?.select());
      }
    },
    [t],
  );

  const visible = useMemo(
    () => data.items.filter((r) => direction === "all" || r.direction === direction),
    [data.items, direction],
  );

  const copyAll = useCallback(async () => {
    // The endpoint returns the joined block AND writes the single CallSummaryCopied audit event. Joining the
    // rows client-side would produce the same text and no audit record — and the audit is the point.
    const result = await api.copyCallSummaries(beneficiaryId, visible.map((r) => r.callRef));
    await copy(result.copyText, STR.copiedAll);
  }, [api, beneficiaryId, copy, visible]);

  return (
    <div className="call-history">
      <div className="call-history-toolbar">
        <label>
          <span className="visually-hidden">{t({ en: "Filter by direction", ar: "تصفية حسب الاتجاه" })}</span>
          <select value={direction} onChange={(e) => setDirection(e.target.value as typeof direction)}>
            <option value="all">{t({ en: "All calls", ar: "كل المكالمات" })}</option>
            <option value="Inbound">{t(STR.inbound)}</option>
            <option value="Outbound">{t(STR.outbound)}</option>
          </select>
        </label>
        {visible.length > 0 ? (
          <Button variant="secondary" onClick={copyAll}>
            {t(STR.copyAll)}
          </Button>
        ) : null}
      </div>

      {visible.length === 0 ? (
        <p className="profile-empty">{t(STR.empty)}</p>
      ) : (
        <ul className="call-history-rows">
          {visible.map((row) => (
            <CallRow key={row.callRef} row={row} onCopy={copy} />
          ))}
        </ul>
      )}

      {/* The outcome of a copy is announced, not merely toasted — a toast is invisible to a screen reader
          and gone before a keyboard user reaches it. */}
      <p aria-live="polite" role="status" className="call-history-announce">
        {announcement}
      </p>

      {fallbackText !== null ? (
        <div role="dialog" aria-modal="true" aria-label={t(STR.copyFallback)} className="copy-fallback">
          <p>{t(STR.copyFallback)}</p>
          <textarea ref={fallbackRef} readOnly value={fallbackText} rows={6} />
          <Button variant="secondary" onClick={() => setFallbackText(null)}>
            {t(STR.close)}
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function CallRow({
  row,
  onCopy,
}: {
  row: CallHistoryRow;
  onCopy: (text: string, announce: Localized) => void | Promise<void>;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const fmt = useFormat();

  // useFormat is the only sanctioned path: a bare toLocaleString formats in the MACHINE's zone and the
  // BROWSER's locale, and neither is the right answer. A pasted call block two hours off matches the wrong
  // call, which is the failure this rule exists to prevent.
  const when = fmt.dateTime(row.startedAt);

  // The accessible name IDENTIFIES the call. "Copy" repeated down a list tells a screen-reader user which
  // button they are on and nothing about which call it copies.
  const copyLabel =
    lang === "ar"
      ? `نسخ ملخص المكالمة ال${row.direction === "Inbound" ? "واردة" : "صادرة"} بتاريخ ${when}`
      : `Copy summary of ${row.direction.toLowerCase()} call on ${when}`;

  return (
    <li className="call-row">
      <div className="call-row-head">
        <DirectionChip direction={row.direction} />
        <span className="call-row-when">{when}</span>
        {row.durationSeconds !== undefined ? (
          <span className="call-row-duration">
            {Math.floor(row.durationSeconds / 60)}m {row.durationSeconds % 60}s
          </span>
        ) : null}
        {row.branchCode ? <span>{row.branchCode}</span> : null}
        {row.reasonCode ? <span className="profile-chip profile-chip--neutral">{row.reasonCode}</span> : null}
        {row.outcome ? <span className="profile-chip profile-chip--info">{row.outcome}</span> : null}
      </div>

      {/* Absence of a summary is INFORMATION — this viewer's level does not carry one. Rendering an empty
          line would read as "the agent wrote nothing", which is a different and untrue statement. */}
      {row.summary ? (
        <p className="call-row-summary">
          {row.summary}
          {row.summaryEdited ? <em className="call-row-edited"> ({t(STR.edited)})</em> : null}
        </p>
      ) : (
        <p className="call-row-summary call-row-summary--withheld">{t(STR.noSummaryAtLevel)}</p>
      )}

      {row.linkedArtifacts?.length ? (
        <ul className="call-row-links">
          {row.linkedArtifacts.map((a) => (
            <li key={`${a.type}-${a.ref}`}>
              {a.type}: {a.ref}
              {a.action ? ` (${a.action})` : ""}
            </li>
          ))}
        </ul>
      ) : null}

      <button
        type="button"
        className="call-row-copy"
        aria-label={copyLabel}
        onClick={() => void onCopy(row.copyText, STR.copied)}
      >
        <span aria-hidden="true">⧉</span> {t(STR.copy)}
      </button>
    </li>
  );
}

// ---------------------------------------------------------------- the patient context bar

/**
 * The compact identity strip that follows a user into encounter, order, dispense, approval and call-centre
 * screens (design 39 §6).
 *
 * <b>Treat it as a safety control, not a convenience.</b> It is how a clinician knows which record is on
 * screen; the failure it prevents is prescribing for the wrong person. It asks for `header,alerts` only —
 * it is on every clinical screen and cannot be slow (build prompt 20.5: p95 &lt; 400ms).
 */
export function PatientContextBar({ beneficiaryId }: { beneficiaryId: string }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync(
    useCallback(() => api.patientProfile(beneficiaryId, ["header", "alerts"]), [api, beneficiaryId]),
    [beneficiaryId],
  );

  // The bar renders nothing until it has an answer, and nothing if the header was withheld. A context bar
  // showing a PARTIAL identity would be worse than none: its entire job is confirming which record is open.
  if (state.status !== "success" || !state.data) return null;
  const profile = state.data;
  const header = profile.sections.find((s) => s.key === "header");
  if (!header || header.state !== "Visible") return null;
  const data = header.data as ProfileHeader;

  const alerts = profile.sections.find((s) => s.key === "alerts");
  const alertCount =
    alerts?.state === "Visible"
      ? ((alerts.data as ProfileAlerts).allergies.length +
          ((alerts.data as ProfileAlerts).criticalFlags?.length ?? 0))
      : 0;

  return (
    <aside className="patient-context-bar" aria-label={t(STR.title)}>
      <Avatar photoUrl={data.photoUrl} name={data.displayName} />
      <a href={`/patients/${encodeURIComponent(beneficiaryId)}`} className="context-bar-name">
        {data.displayName}
      </a>
      <span className="context-bar-meta">
        {[data.memberNo, data.ageBand, data.sex].filter(Boolean).join(" · ")}
      </span>
      <span className={`profile-chip profile-chip--${data.statusCue.tone}`} data-shape={data.statusCue.shape}>
        <span aria-hidden="true">●</span> {data.statusCue.label}
      </span>
      {alertCount > 0 ? (
        <span className="profile-chip profile-chip--critical" data-shape="octagon">
          <span aria-hidden="true">⚠</span> {alertCount} {t(STR.alerts)}
        </span>
      ) : null}
    </aside>
  );
}

export default PatientProfile;

/** Re-exported so tests and other screens assert against the same titles the screen renders. */
export const PROFILE_SECTION_TITLES: Record<string, Localized> = SECTION_TITLES;
