import { Fragment } from "react";
import { InlineAlert, useTheme } from "@mersal/design-system";
import { useFormat } from "../../i18n/useFormat";
import { useLoc } from "../_shared";
import type { Localized } from "../../portals/catalog";

const S = {
  empty: {
    en: "No changes have been recorded yet.",
    ar: "لم تُسجَّل أي تغييرات بعد.",
  },
  created: { en: "Created", ar: "أُنشئ" },
  changed: { en: "Changed", ar: "عُدّل" },
  by: { en: "by", ar: "بواسطة" },
  unknownActor: { en: "an unnamed account", ar: "حساب غير مسمّى" },
  nothingChanged: {
    en: "Recorded, with no change to the values shown here.",
    ar: "سُجّل دون تغيير في القيم المعروضة هنا.",
  },
  notSet: { en: "not set", ar: "غير محدد" },
  arrow: { en: "→", ar: "←" },
  // The audit trail is a DIFFERENT record for different people, and saying so on screen stops this being
  // mistaken for the compliance answer to a compliance question.
  notTheAuditTrail: {
    en: "This is the clinic's own record of changes. The formal audit trail is held separately and is read by the compliance team.",
    ar: "هذا سجل التغييرات الخاص بالعيادة. أما سجل التدقيق الرسمي فيُحفظ بشكل منفصل ويطّلع عليه فريق الالتزام.",
  },
} satisfies Record<string, Localized>;

/**
 * One row of a change timeline, reduced to the fields this timeline is about.
 *
 * Deliberately NOT the aggregate's own type. Availability, licences and roster exceptions each have their own
 * shape and their own history endpoint, and the thing they share is the SEQUENCE — who, when, and what the
 * values were afterwards. Modelling that shared part here is what lets one component serve all three; a
 * union of the three payloads would be three components wearing one name.
 */
export interface TimelineEntry {
  sequence: number;
  recordedAt: string;
  actorName?: string | null;
  actorSubject?: string | null;
  /** Label → value, in display order. Values are what they were AFTER this change. */
  values: Array<{ label: Localized; value: string | null }>;
}

/**
 * The change timeline for one administered record (design 42 §7 rule 14).
 *
 * <b>What it is for.</b> Licence, roster and availability changes have always been audited — into the
 * hash-chained store behind `audit:read`, which is Security, Compliance and the DPO. Correctly: it is
 * tamper-evident evidence and its own reads are audited. It also left the person who RUNS the clinic unable
 * to ask who narrowed their Tuesday or who last renewed a licence, about records they administer themselves.
 * This reads the domain history instead, under the same branch reach as the record.
 *
 * <b>Diffs are computed HERE, from values.</b> Each endpoint returns the state after each change and this
 * compares adjacent entries. One implementation of "what changed", used by all three timelines — computing it
 * per service would put three subtly different notions of the same idea in three places, and the one that
 * disagreed would be the one nobody was reading.
 *
 * <b>Newest first.</b> The APIs return ascending sequence because that is the natural order of a log; a
 * reader opening this wants the most recent change, so it is reversed for display. The FIRST entry is still
 * labelled "Created" — that is a fact about the record, not about the scroll position.
 */
export function ChangeTimeline({ entries }: { entries: readonly TimelineEntry[] }) {
  const t = useLoc();
  const { lang } = useTheme();
  const fmt = useFormat();

  if (entries.length === 0) {
    return <InlineAlert tone="info">{t(S.empty)}</InlineAlert>;
  }

  const ascending = [...entries].sort((a, b) => a.sequence - b.sequence);

  return (
    <div className="change-timeline">
      <ol className="change-timeline__list">
        {ascending
          .map((entry, index) => ({ entry, previous: index === 0 ? null : ascending[index - 1] }))
          .reverse()
          .map(({ entry, previous }) => {
            const changes = diff(entry, previous);
            const actor = entry.actorName?.trim() || entry.actorSubject?.trim();

            return (
              <li key={entry.sequence} className="change-timeline__entry">
                <p className="change-timeline__when">
                  <strong>{previous === null ? t(S.created) : t(S.changed)}</strong>
                  {" · "}
                  {/* Cairo-pinned via useFormat. A bare toLocaleString renders in the MACHINE's zone, so a
                      clinic PC set to UTC would date every change two hours early — and near midnight, a day
                      early, which is exactly the kind of detail a timeline is consulted for. */}
                  {fmt.dateTime(entry.recordedAt)}
                  {" · "}
                  {t(S.by)}{" "}
                  {/* A history row that cannot name its actor says so, rather than rendering an empty space
                      that reads as a rendering fault. Entries predating the actor columns are exactly this. */}
                  {actor ?? <span className="muted">{t(S.unknownActor)}</span>}
                </p>

                {changes.length === 0 ? (
                  <p className="muted">{t(S.nothingChanged)}</p>
                ) : (
                  <ul className="change-timeline__changes">
                    {changes.map((c) => (
                      <li key={c.label.en}>
                        <span className="change-timeline__field">{t(c.label)}</span>{" "}
                        {previous === null ? (
                          <strong>{c.to ?? t(S.notSet)}</strong>
                        ) : (
                          <Fragment>
                            {/* The old value stays on screen beside the new one. "Cap: 12" answers what it is
                                now; "20 → 12" answers what someone did, which is the question a timeline is
                                open for. */}
                            <span className="change-timeline__from">{c.from ?? t(S.notSet)}</span>{" "}
                            {/* Directional, and it mirrors in Arabic along with the layout. */}
                            <span aria-hidden="true">{t(S.arrow)}</span>{" "}
                            <strong>{c.to ?? t(S.notSet)}</strong>
                          </Fragment>
                        )}
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            );
          })}
      </ol>

      <p className="muted change-timeline__note" lang={lang}>{t(S.notTheAuditTrail)}</p>
    </div>
  );
}

interface FieldChange {
  label: Localized;
  from: string | null;
  to: string | null;
}

/**
 * What this entry changed, relative to the one before it.
 *
 * The first entry reports every value it set — there is nothing to compare against, and a creation that
 * listed no changes would render as an empty event. After that only DIFFERENCES are listed: a timeline that
 * repeats every unchanged field on every entry buries the one thing that moved.
 */
function diff(entry: TimelineEntry, previous: TimelineEntry | null): FieldChange[] {
  if (previous === null) {
    return entry.values
      .filter((v) => v.value !== null)
      .map((v) => ({ label: v.label, from: null, to: v.value }));
  }

  const before = new Map(previous.values.map((v) => [v.label.en, v.value]));
  return entry.values
    .filter((v) => before.get(v.label.en) !== v.value)
    .map((v) => ({ label: v.label, from: before.get(v.label.en) ?? null, to: v.value }));
}
