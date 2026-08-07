using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Emr.Infrastructure;

/// <summary>EF Core context for the <c>emr</c> schema. Phase 2.3: encounter shell + clinician queue.</summary>
public sealed class EmrDbContext(DbContextOptions<EmrDbContext> options) : DbContext(options)
{
    public const string Schema = "emr";

    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentSlot> AppointmentSlots => Set<AppointmentSlot>();
    public DbSet<ProviderAvailability> ProviderAvailabilities => Set<ProviderAvailability>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<EmrNote> Notes => Set<EmrNote>();
    public DbSet<Diagnosis> Diagnoses => Set<Diagnosis>();
    public DbSet<Vital> Vitals => Set<Vital>();
    public DbSet<Allergy> Allergies => Set<Allergy>();
    public DbSet<MedicationHistory> MedicationHistories => Set<MedicationHistory>();
    /// <summary>Standing per-person clinical facts — blood group today (migration 0021).</summary>
    public DbSet<BeneficiaryClinical> BeneficiaryClinical => Set<BeneficiaryClinical>();

    /// <summary>25.4 — leave, holidays, closures and ad-hoc clinics (design 42 §4). The exception layer the
    /// weekly recurring rule never had; without it the only way to stop slots was to delete the rule.</summary>
    public DbSet<RosterException> RosterExceptions => Set<RosterException>();
    /// <summary>The care-episode timeline (ADR-0031) — append-only, one row per step.</summary>
    public DbSet<CareStep> CareTimeline => Set<CareStep>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("emr");
        b.HasDefaultSchema(Schema);

        b.Entity<Appointment>(e =>
        {
            e.ToTable("appointment");
            e.HasKey(x => x.AppointmentId);
            e.Property(x => x.AppointmentType).HasConversion<string>().HasColumnName("appointment_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            // xmin optimistic-concurrency token (feeds the 3.2 If-Match ETag).
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            // At most one active appointment per slot (partial unique index created in SQL).
            e.HasIndex(x => x.SlotId);
            e.HasIndex(x => x.IdempotencyKey);
            e.Property(x => x.BranchId).HasColumnName("branch_id");   // phase 14
            e.Property(x => x.DoctorId).HasColumnName("doctor_id");   // phase 23 — the doctor's own worklist
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by"); // phase 23 — timeline attribution
            // 14.5 — the general/administrative booking note. Capped here as well as in migration 0011: the
            // model and the schema must not be able to disagree about what fits.
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(AppointmentNote.MaxLength);
            e.Property(x => x.ReassignmentNeededAt).HasColumnName("reassignment_needed_at");   // 14.5
            e.Property(x => x.BeneficiaryName).HasColumnName("beneficiary_name");               // 14.5 (0013)
            e.Property(x => x.NoteBy).HasColumnName("note_by");                                 // 14.5 (0014)
            e.Property(x => x.NoteAt).HasColumnName("note_at");
            e.Property(x => x.NoteByName).HasColumnName("note_by_name").HasMaxLength(160);      // 0022
            e.HasIndex(x => new { x.BranchId, x.ScheduledStart });
            e.HasIndex(x => new { x.DoctorId, x.ScheduledStart });
        });

        b.Entity<AppointmentSlot>(e =>
        {
            e.ToTable("appointment_slot");
            e.HasKey(x => x.SlotId);
            e.Property(x => x.BranchId).HasColumnName("branch_id");   // phase 14
            e.HasIndex(x => new { x.ProviderId, x.LocationId, x.SlotStart });
        });

        b.Entity<ProviderAvailability>(e =>
        {
            e.ToTable("provider_availability");
            e.HasKey(x => x.AvailabilityId);
            e.Property(x => x.DayOfWeek).HasConversion<int>().HasColumnName("day_of_week");
            e.Property(x => x.StartTime).HasColumnName("start_time");
            e.Property(x => x.EndTime).HasColumnName("end_time");
            e.Property(x => x.BranchId).HasColumnName("branch_id");   // phase 14
        });

        b.Entity<RosterException>(e =>
        {
            e.ToTable("roster_exception");
            e.HasKey(x => x.ExceptionId);
            e.Property(x => x.Kind).HasConversion<string>().HasColumnName("kind");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.PractitionerId).HasColumnName("practitioner_id");
            e.Property(x => x.DateFrom).HasColumnName("date_from");
            e.Property(x => x.DateTo).HasColumnName("date_to");
            e.Property(x => x.StartTime).HasColumnName("start_time");
            e.Property(x => x.EndTime).HasColumnName("end_time");
            e.Ignore(x => x.IsSubtractive);
            e.Ignore(x => x.IsWholeDay);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<WaitlistEntry>(e =>
        {
            e.ToTable("waitlist_entry");
            e.HasKey(x => x.WaitlistId);
            e.Property(x => x.AppointmentType).HasConversion<string>().HasColumnName("appointment_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.BranchId).HasColumnName("branch_id");   // phase 14
            e.HasIndex(x => new { x.ProviderId, x.LocationId, x.Status });
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });

        b.Entity<QueueTicket>(e =>
        {
            e.ToTable("appointment_queue");
            e.HasKey(x => x.QueueId);
            e.Property(x => x.AppointmentType).HasConversion<string>().HasColumnName("appointment_type");
            e.Property(x => x.State).HasConversion<string>().HasColumnName("state");
            e.Property(x => x.BranchId).HasColumnName("branch_id");   // phase 14
            e.HasIndex(x => new { x.BranchId, x.State });
            e.HasIndex(x => new { x.LocationId, x.ProviderId, x.State });
        });

        b.Entity<Encounter>(e =>
        {
            e.ToTable("encounter");
            e.HasKey(x => x.EncounterId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.EncounterNo).IsUnique();
            // Idempotent creation: one encounter per Idempotency-Key (partial unique index created in SQL).
            e.HasIndex(x => x.IdempotencyKey);
        });

        b.Entity<CareStep>(e =>
        {
            e.ToTable("care_timeline");
            e.HasKey(x => x.StepId);
            // Both directions are read: the workspace opens an episode from the encounter, the desk's board
            // from the appointment.
            e.HasIndex(x => new { x.EncounterId, x.OccurredAt });
            e.HasIndex(x => new { x.AppointmentId, x.OccurredAt });
            // At-least-once delivery means the same event can arrive twice; the partial unique index in
            // migration 0019 is what actually enforces this.
            e.HasIndex(x => x.EventId).IsUnique();
        });

        b.Entity<QueueEntry>(e =>
        {
            e.ToTable("queue_entry");
            e.HasKey(x => x.QueueEntryId);
            e.Property(x => x.State).HasConversion<string>().HasColumnName("state");
            e.HasIndex(x => x.EncounterId);
        });

        b.Entity<EmrNote>(e =>
        {
            e.ToTable("emr_note");
            e.HasKey(x => x.NoteId);
            e.Property(x => x.NoteType).HasConversion<string>().HasColumnName("note_type");
            e.HasIndex(x => x.EncounterId);
        });

        b.Entity<Diagnosis>(e =>
        {
            e.ToTable("diagnosis");
            e.HasKey(x => x.DiagnosisId);
            e.Property(x => x.DiagnosisRank).HasConversion<string>().HasColumnName("diagnosis_rank");
            e.Property(x => x.ClinicalStatus).HasConversion<string>().HasColumnName("clinical_status");
            e.HasIndex(x => x.EncounterId);
        });

        b.Entity<Vital>(e =>
        {
            e.ToTable("vital");
            e.HasKey(x => x.VitalId);
            e.Property(x => x.VitalType).HasConversion<string>().HasColumnName("vital_type");
            e.HasIndex(x => x.EncounterId);
        });

        b.Entity<Allergy>(e =>
        {
            e.ToTable("allergy");
            e.HasKey(x => x.AllergyId);
            e.Property(x => x.Severity).HasConversion<string>().HasColumnName("severity");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.BeneficiaryId);
        });

        b.Entity<BeneficiaryClinical>(e =>
        {
            e.ToTable("beneficiary_clinical");
            e.HasKey(x => x.BeneficiaryId);   // one row per person — the PK IS the beneficiary
        });

        b.Entity<MedicationHistory>(e =>
        {
            e.ToTable("medication_history");
            e.HasKey(x => x.MedHistoryId);
            e.Property(x => x.Source).HasConversion<string>().HasColumnName("source");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.BeneficiaryId);
        });
    }
}
