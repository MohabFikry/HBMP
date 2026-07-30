import { useMemo } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, DataTable, Icon, InlineAlert, KpiCard, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentCounts, AppointmentRow, Localized, Practitioner, Specialty } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { AppointmentNoteButton } from "./AppointmentNote";

const S = {
  title: { en: "Dashboard", ar: "لوحة المتابعة" },

  cardTotal: { en: "Appointments today", ar: "مواعيد اليوم" },
  cardCheckedIn: { en: "Checked in", ar: "تم الوصول" },
  cardNoShow: { en: "No-shows", ar: "لم يحضروا" },
  countsFailed: {
    en: "Couldn't load today's figures — the cards below are not current.",
    ar: "تعذّر تحميل أرقام اليوم — البطاقات أدناه ليست محدّثة.",
  },
  retry: { en: "Retry", ar: "إعادة المحاولة" },

  visitsHeading: { en: "Today's visits", ar: "زيارات اليوم" },
  visitsEmpty: { en: "No one has checked in yet today.", ar: "لم يسجّل أحد وصوله اليوم بعد." },
  patient: { en: "Patient", ar: "المريض" },
  doctor: { en: "Doctor", ar: "الطبيب" },
  specialty: { en: "Specialty", ar: "التخصص" },
  time: { en: "Time", ar: "الوقت" },
  note: { en: "Note", ar: "ملاحظة" },
  openFile: { en: "Patient file", ar: "ملف المريض" },
  unnamedDoctor: { en: "No named doctor", ar: "بدون طبيب محدد" },

  calendarHeading: { en: "Today's schedule", ar: "جدول اليوم" },
  calendarEmpty: { en: "No appointments booked for today.", ar: "لا توجد مواعيد محجوزة اليوم." },
  unscheduled: { en: "Other", ar: "أخرى" },
} satisfies Record<string, Localized>;

/** The clinic's working span. Anything outside it falls into a final "Other" band rather than vanishing. */
const HOURS = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];

/**
 * The reception dashboard (14.5).
 *
 * Three things the desk needs at a glance and previously had to assemble by eye across two screens: how the
 * day is going, who is in the building right now, and what is still to come.
 *
 * <b>On showing patient NAMES here.</b> Reception's other boards render a masked token, and that is right for
 * them. This section is different and the difference was signed off: the desk greets the person, walks them
 * to a room and arranges the rest of their journey, and none of that is possible against "•••4821". The name
 * is only present for patients who have ARRIVED — it is captured at check-in — so this list is exactly the
 * set of people physically in the building, which is also the narrowest set that answers the need.
 *
 * <b>Doctor and specialty are a client-side join.</b> emr holds a `doctorId`; who that is belongs to
 * provider-service, which reception reads directly under `practitioner:read`. Neither service composes the
 * other's data on the caller's behalf — see `bookableDoctors` for the same shape on the booking screen.
 */
export function ReceptionDashboard() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const navigate = useNavigate();

  const counts = useAsync<AppointmentCounts>(() => api.appointmentCounts(), []);
  const board = useAsync<AppointmentRow[]>(() => api.appointments("all"), []);
  const doctors = useAsync<Practitioner[]>(() => api.practitioners({ type: "Doctor" }), []);
  const specialties = useAsync<Specialty[]>(() => api.specialties(), []);

  const doctorById = useMemo(
    () => new Map((doctors.data ?? []).map((d) => [d.id, d])),
    [doctors.data],
  );
  const specialtyName = useMemo(() => {
    const m = new Map((specialties.data ?? []).map((s) => [s.code, s.name]));
    // The code is the honest fallback while the reference list loads — a dash would claim the doctor has no
    // specialty, which is a different and worse statement.
    return (code?: string) => (code ? (m.get(code) ? t(m.get(code)!) : code) : null);
  }, [specialties.data, t]);

  const visits = useMemo(() => (board.data ?? []).filter((r) => r.checkedIn), [board.data]);

  /**
   * What a card shows. Zero is a NUMBER, not a dash — "0 appointments today" is a real and useful answer,
   * and rendering it as "—" would tell the desk the figure is unknown when it is in fact known and quiet.
   */
  const cardValue = (n?: number) =>
    counts.status === "loading" ? "…" : counts.status === "error" ? "—" : String(n ?? 0);

  const visitCols: Column<AppointmentRow>[] = [
    {
      key: "patient", header: t(S.patient), sortable: true,
      // Falls back to the masked token rather than blank: a row with no name is still a real person the desk
      // has to be able to identify.
      cell: (r) => <strong>{r.beneficiaryName ?? <span className="tnum">{r.beneficiary.token}</span>}</strong>,
      sortValue: (r) => r.beneficiaryName ?? r.beneficiary.token,
    },
    {
      key: "doctor", header: t(S.doctor), sortable: true,
      cell: (r) => {
        const d = r.doctorId ? doctorById.get(r.doctorId) : undefined;
        return d ? t(d.name) : <span className="muted">{t(S.unnamedDoctor)}</span>;
      },
      sortValue: (r) => (r.doctorId ? doctorById.get(r.doctorId)?.name.en : undefined),
    },
    {
      key: "specialty", header: t(S.specialty), sortable: true,
      cell: (r) => {
        const name = specialtyName(r.doctorId ? doctorById.get(r.doctorId)?.primarySpecialty : undefined);
        return name ?? <span className="muted">—</span>;
      },
      sortValue: (r) => specialtyName(r.doctorId ? doctorById.get(r.doctorId)?.primarySpecialty : undefined),
    },
    {
      key: "time", header: t(S.time), sortable: true,
      cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span>,
      sortValue: (r) => r.scheduledStart,
    },
    { key: "note", header: t(S.note), cell: (r) => <AppointmentNoteButton note={r.note} /> },
    {
      key: "file",
      header: t(S.openFile),
      // Icon + primary: this is the action the desk reaches for most, and as a plain secondary among
      // several it read as the least important thing in the row.
      cell: (r) => (
        <Button
          variant="primary"
          size="sm"
          leadingIcon={<Icon name="user" />}
          onClick={() => navigate(`/patients/${encodeURIComponent(r.beneficiary.id)}`)}
        >
          {t(S.openFile)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />

      {/* ── Cards ──────────────────────────────────────────────────────
          Counted server-side. Tallying the board here would be capped at its 200-row page and would
          undercount a busy day, in the direction nobody checks. */}
      <div className="dash-kpis">
        <KpiCard label={t(S.cardTotal)} value={cardValue(counts.data?.total)} />
        <KpiCard label={t(S.cardCheckedIn)} value={cardValue(counts.data?.checkedIn)} />
        <KpiCard label={t(S.cardNoShow)} value={cardValue(counts.data?.noShow)} />
      </div>
      {/*
        A failed count is SAID, not implied by a dash.

        These three cards previously rendered `?? "—"`, which collapsed three different situations into one
        glyph: still loading, failed to load, and a genuinely quiet morning with zero appointments. A desk
        reading "—" cannot tell "we don't know" from "there are none", and the second reading is the
        dangerous one — it invites someone to conclude the day is empty when the figure simply never arrived.
        Zero now renders as 0, loading renders as an ellipsis, and a failure says so and offers a retry.
      */}
      {counts.status === "error" && (
        <div role="alert" style={{ marginTop: "var(--sp2)" }}>
          <InlineAlert tone="bad">
            <span>{t(S.countsFailed)}</span>{" "}
            <Button variant="secondary" size="sm" onClick={counts.reload}>{t(S.retry)}</Button>
          </InlineAlert>
        </div>
      )}

      {/* ── Today's visits ─────────────────────────────────────────── */}
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp4)" }}>
        <h2 className="section-h">{t(S.visitsHeading)}</h2>
        <AsyncSection<AppointmentRow[]> state={board} isEmpty={() => visits.length === 0} emptyLabel={S.visitsEmpty}>
          {() => (
            <DataTable columns={visitCols} rows={visits} rowKey={(r) => r.id} caption={t(S.visitsHeading)} />
          )}
        </AsyncSection>
      </Card>

      {/* ── The day, laid out ──────────────────────────────────────── */}
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp4)" }}>
        <h2 className="section-h">{t(S.calendarHeading)}</h2>
        <AsyncSection<AppointmentRow[]> state={board} isEmpty={(d) => d.length === 0} emptyLabel={S.calendarEmpty}>
          {(rows) => <DaySchedule rows={rows} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/**
 * The clinic's day as hour bands.
 *
 * A calendar rather than a third table because the question it answers is shaped differently: not "what is
 * this appointment" but "when is the desk about to be busy". Bands are laid out from a FIXED hour list, so an
 * empty 11:00 renders as a visible gap — which is the answer to "can we fit a walk-in in?" and is exactly
 * what a list of only-the-booked-hours cannot show.
 */
function DaySchedule({ rows }: { rows: AppointmentRow[] }) {
  const t = useLoc();
  const fmt = useFormat();

  const byHour = useMemo(() => {
    const m = new Map<number | "other", AppointmentRow[]>();
    for (const r of rows) {
      // The CAIRO hour, not the browser's: a clinic PC on UTC would file a 09:00 appointment under 07:00 and
      // draw a schedule two bands out of step with every time printed on it.
      const hour = Number(
        new Intl.DateTimeFormat("en-GB", { timeZone: "Africa/Cairo", hour: "2-digit", hour12: false })
          .format(new Date(r.scheduledStart)),
      );
      const key: number | "other" = HOURS.includes(hour) ? hour : "other";
      m.set(key, [...(m.get(key) ?? []), r]);
    }
    return m;
  }, [rows]);

  const other = byHour.get("other") ?? [];

  return (
    <ol className="dash-day">
      {HOURS.map((h) => {
        const at = byHour.get(h) ?? [];
        return (
          <li key={h} className="dash-hour">
            <span className="dash-hour-label tnum">{String(h).padStart(2, "0")}:00</span>
            <div className="dash-hour-items">
              {at.map((r) => (
                <span key={r.id} className="dash-appt">
                  <span className="tnum">{fmt.time(r.scheduledStart)}</span>
                  <span>{r.beneficiaryName ?? r.beneficiary.token}</span>
                  <StatusChip kind={r.status.kind} label={t(r.status.label)} />
                </span>
              ))}
            </div>
          </li>
        );
      })}
      {/* Anything outside clinic hours. Shown rather than dropped: an appointment the schedule silently
          omitted is one nobody prepares for. */}
      {other.length > 0 && (
        <li className="dash-hour">
          <span className="dash-hour-label">{t(S.unscheduled)}</span>
          <div className="dash-hour-items">
            {other.map((r) => (
              <span key={r.id} className="dash-appt">
                <span className="tnum">{fmt.time(r.scheduledStart)}</span>
                <span>{r.beneficiaryName ?? r.beneficiary.token}</span>
                <StatusChip kind={r.status.kind} label={t(r.status.label)} />
              </span>
            ))}
          </div>
        </li>
      )}
    </ol>
  );
}
