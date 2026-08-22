import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import {
  Button, Card, Icon, InlineAlert, InputField, SearchField, StatusChip,
} from "@mersal/design-system";
import type { Localized, ProviderReadiness, ProviderSummary } from "@mersal/contracts";
import { createHttpNetworkApi } from "../api/networkApi";
import type { NetworkApi } from "../api/networkApi";
import { HistoryModal, type AdminHistoryEntry } from "./AdminRecordControls";
import { useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";

/**
 * Phase 19.9 — the pieces the four network-portal sections share (design 58).
 *
 * ============================================================================================================
 * WHY A SHARED MODULE AND NOT FOUR COPIES
 * ============================================================================================================
 * Directory, Onboarding, Contracts and Locations all begin the same way — choose a provider — and all end the
 * same way: show what changed and who changed it. Written four times, those drift; the UI/UX audit that ran
 * before 19.8 counted what that costs when it happens (eight wrapper classes for one checkbox, five ways of
 * building a filter bar), and the fix that stuck was a module, not a convention.
 */

/** ONE client for the module. A fresh instance per render turns any effect keyed on it into a request loop. */
export const networkApi: NetworkApi = createHttpNetworkApi();

/**
 * The provider types the portal offers.
 *
 * `Radiology` is the canonical spelling since 29.1 and `Imaging` is retained for the duration of the
 * expand/contract window (design 45 §1). Both are offered because a provider onboarded before the switch
 * still carries the old one and has to be editable without silently being retyped.
 */
export const PROVIDER_TYPES = ["Hospital", "Clinic", "Lab", "Pharmacy", "Radiology", "Imaging"] as const;

export const TYPE_LABEL: Record<string, Localized> = {
  Hospital: { en: "Hospital", ar: "مستشفى" },
  Clinic: { en: "Doctor / Clinic", ar: "طبيب / عيادة" },
  Lab: { en: "Laboratory", ar: "معمل" },
  Pharmacy: { en: "Pharmacy", ar: "صيدلية" },
  Radiology: { en: "Imaging centre", ar: "مركز أشعة" },
  Imaging: { en: "Imaging centre", ar: "مركز أشعة" },
};

const S = {
  pick: { en: "Choose a provider", ar: "اختر مقدم خدمة" },
  pickHint: { en: "Name or provider code", ar: "الاسم أو رمز مقدم الخدمة" },
  pickPrompt: {
    en: "Choose a provider to administer.",
    ar: "اختر مقدم خدمة لإدارته.",
  },
  change: { en: "Change provider", ar: "تغيير مقدم الخدمة" },
  noProviders: { en: "No providers in this network.", ar: "لا يوجد مقدمو خدمة في هذه الشبكة." },
  noMatch: { en: "No provider matches that search.", ar: "لا يوجد مقدم خدمة مطابق لهذا البحث." },
  showing: { en: "Showing {0} of {1}", ar: "عرض {0} من {1}" },
  // ── readiness ───────────────────────────────────────────────────────────────────────────────────────────
  readiness: { en: "Ready to go live?", ar: "جاهز للتفعيل؟" },
  readinessHint: {
    en: "Activation needs all four. They are checked by the server at the moment you activate — this is the same check, asked early.",
    ar: "التفعيل يتطلب الأربعة جميعًا. يتحقق منها الخادم لحظة التفعيل — وهذا هو الفحص نفسه، مطروحًا مبكرًا.",
  },
  hasPrimaryLocation: { en: "A primary location", ar: "موقع رئيسي" },
  hasMandatoryCredentials: { en: "Mandatory documents attached", ar: "المستندات الإلزامية مرفقة" },
  mandatoryCredentialsValid: { en: "None of them expired", ar: "لم ينتهِ أي منها" },
  hasActiveContract: { en: "A contract in effect today", ar: "عقد ساري اليوم" },
  ready: { en: "Ready to activate", ar: "جاهز للتفعيل" },
  notReady: { en: "Not ready", ar: "غير جاهز" },
  met: { en: "Done", ar: "مكتمل" },
  notMet: { en: "Outstanding", ar: "غير مكتمل" },
  // ── history ─────────────────────────────────────────────────────────────────────────────────────────────
  changedFrom: { en: "was {0}", ar: "كان {0}" },
  notRecorded: { en: "—", ar: "—" },
} satisfies Record<string, Localized>;

// ── The provider picker ─────────────────────────────────────────────────────────────────────────────────

/**
 * Choose one provider, then work on it.
 *
 * <p>The picker this replaces rendered every provider in the tenant as one unbroken list of buttons, with no
 * search and no filter — the same fault the Providers Directory had before 33.7 and which was fixed there and
 * not here. A network of two hundred is a scroll; a network of two hundred is also the only kind of network
 * this screen exists for.</p>
 *
 * <p>The search matches the CODE as well as the name because a contract, a claim and an invoice all cite the
 * code, so it is what an operator is holding when they arrive at this screen.</p>
 */
export function ProviderScope({
  providers, picked, onPick, title, children,
}: {
  providers: ProviderSummary[] | null;
  picked: ProviderSummary | null;
  onPick: (p: ProviderSummary | null) => void;
  title: string;
  children: (p: ProviderSummary) => ReactNode;
}) {
  const t = useLoc();
  const [term, setTerm] = useState("");

  // Memoised so the `??` does not hand `matches` a fresh array identity on every render.
  const rows = useMemo(() => providers ?? [], [providers]);
  const matches = useMemo(() => {
    const q = term.trim().toLowerCase();
    if (!q) return rows;
    return rows.filter((p) => `${p.legalName} ${p.code} ${p.providerType}`.toLowerCase().includes(q));
  }, [rows, term]);

  if (picked) {
    return (
      <div className="pol-stack">
        <div className="screen-toolbar">
          <div className="pay-head">
            <h2 style={{ margin: 0 }}>{picked.legalName}</h2>
            <div className="pay-chips">
              <span className="tnum pol-muted">{picked.code}</span>
              <StatusChip kind={picked.status.kind} label={t(picked.status.label)} />
            </div>
          </div>
          <Button variant="ghost" leadingIcon={<Icon name="refer" />} onClick={() => onPick(null)}>
            {t(S.change)}
          </Button>
        </div>
        {children(picked)}
      </div>
    );
  }

  return (
    <Card as="section" aria-label={title}>
      <div className="pol-stack">
        <p className="pol-muted" style={{ margin: 0 }}>{t(S.pickPrompt)}</p>
        <SearchField
          aria-label={t(S.pick)}
          placeholder={t(S.pickHint)}
          value={term}
          onChange={(e) => setTerm(e.currentTarget.value)}
        />
        {rows.length === 0 && <InlineAlert tone="info">{t(S.noProviders)}</InlineAlert>}
        {rows.length > 0 && matches.length === 0 && <InlineAlert tone="info">{t(S.noMatch)}</InlineAlert>}
        {matches.length > 0 && (
          <ul className="net-picker mrs-scroll" aria-label={t(S.pick)}>
            {matches.map((p) => (
              <li key={p.id}>
                <button type="button" className="picker-row" onClick={() => onPick(p)}>
                  <span>{p.legalName}</span>
                  <span className="tnum pol-muted">{p.code}</span>
                  <StatusChip kind={p.status.kind} label={t(p.status.label)} />
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Card>
  );
}

// ── The readiness checklist ─────────────────────────────────────────────────────────────────────────────

/**
 * The four conditions activation is guarded on, shown before anybody presses activate.
 *
 * <p>The server has always evaluated all four and answered a blocked attempt with the FIRST one that failed,
 * as a sentence, in a 422. An operator fixing a provider therefore learned about one missing thing per
 * attempt, and only by attempting. The endpoint now returns the whole set; this renders it.</p>
 *
 * <p>Each row carries hue, icon, shape and text — the four-cue rule — so "outstanding" survives greyscale
 * and colour blindness. The list is not a substitute for the server's check: activation still asks, and the
 * refusal is still the server's own wording.</p>
 */
export function ReadinessChecklist({ readiness }: { readiness: ProviderReadiness }) {
  const t = useLoc();
  const items: Array<[keyof ProviderReadiness, Localized]> = [
    ["hasPrimaryLocation", S.hasPrimaryLocation],
    ["hasMandatoryCredentials", S.hasMandatoryCredentials],
    ["mandatoryCredentialsValid", S.mandatoryCredentialsValid],
    ["hasActiveContract", S.hasActiveContract],
  ];
  return (
    <div className="pol-stack">
      <div className="pay-chips">
        <h3 style={{ margin: 0 }}>{t(S.readiness)}</h3>
        <StatusChip
          kind={readiness.canActivate ? "ok" : "warn"}
          label={t(readiness.canActivate ? S.ready : S.notReady)}
        />
      </div>
      <p className="pol-muted" style={{ margin: 0 }}>{t(S.readinessHint)}</p>
      <ul className="net-check">
        {items.map(([key, label]) => {
          const met = Boolean(readiness[key]);
          return (
            <li key={String(key)}>
              <StatusChip kind={met ? "ok" : "warn"} label={t(met ? S.met : S.notMet)} />
              <span>{t(label)}</span>
            </li>
          );
        })}
      </ul>
      {/* The server's own sentence, not a re-derivation. If the guard's wording changes, this follows. */}
      {!readiness.canActivate && readiness.blockingReason && (
        <InlineAlert tone="warn">{readiness.blockingReason}</InlineAlert>
      )}
    </div>
  );
}

// ── History ─────────────────────────────────────────────────────────────────────────────────────────────

export interface NetworkHistoryEntry extends AdminHistoryEntry {
  fields: Record<string, string | null>;
}

/**
 * A change timeline over any of the three provider-domain twins.
 *
 * <p>`labels` decides which snapshot fields are shown and what they are called, so one renderer serves the
 * provider, its locations and its contracts. A field absent from an entry is rendered as absent rather than
 * skipped: an entry written before a column existed is exactly the old entry somebody is digging for, and
 * dropping the row would make the timeline look like the field never changed.</p>
 */
export function NetworkHistoryModal({
  title, load, labels, onClose,
}: {
  title: Localized;
  load: () => Promise<{ entries: NetworkHistoryEntry[] }>;
  labels: Record<string, Localized>;
  onClose: () => void;
}) {
  const t = useLoc();
  return (
    <HistoryModal<NetworkHistoryEntry>
      title={title}
      load={load}
      onClose={onClose}
      facts={(e) => (
        <>
          {Object.entries(labels).map(([key, label]) => (
            <div key={key}>
              <dt>{t(label)}</dt>
              <dd>{e.fields?.[key] ?? t(S.notRecorded)}</dd>
            </div>
          ))}
        </>
      )}
    />
  );
}

// ── Small shared bits ───────────────────────────────────────────────────────────────────────────────────

/** The label/value pair every administrative identity list uses. Re-exported rather than redefined: the
 *  audit before 19.8 counted what a second copy of a shared bit costs, and this one is four lines. */
export { Fact } from "./AdminRecordControls";

/** A date the platform's way — Africa/Cairo and the app locale, never the browser's (18.D2 U7). */
export function useDate(): (iso: string | null | undefined) => string {
  const fmt = useFormat();
  return useCallback((iso) => (iso ? fmt.date(iso) : "—"), [fmt]);
}

/** Load-on-mount with a live flag, for the per-section reads that are not worth a `useAsync` state machine. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): [T | null, () => void] {
  const [data, setData] = useState<T | null>(null);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    let live = true;
    void load().then((d) => { if (live) setData(d); }).catch(() => { if (live) setData(null); });
    return () => { live = false; };
    // `load` is a closure over ids the caller lists in `deps`; including it would refetch every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, nonce]);
  return [data, useCallback(() => setNonce((n) => n + 1), [])];
}

/** A number field that reports its own emptiness rather than coercing it to zero. */
export function NumberField({
  label, help, value, onChange, min,
}: { label: string; help?: string; value: string; onChange: (v: string) => void; min?: number }) {
  return (
    <InputField
      label={label}
      help={help}
      type="number"
      inputMode="decimal"
      min={min}
      value={value}
      onChange={(e) => onChange(e.currentTarget.value)}
      style={{ maxInlineSize: "var(--field-max)" }}
    />
  );
}
