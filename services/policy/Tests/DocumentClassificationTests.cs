using System.Text.Json;
using FluentAssertions;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.3b — the classification rule and the download authority (design 38 §5b).
///
/// Classification cannot be left to the uploader's judgement alone, because the failure is SILENT: a past
/// medical history filed as Administrative is readable by finance and the call centre forever, and nothing
/// about the row looks wrong. So the class sets a floor and a human may only ever tighten it.
///
/// The access tests assert over the SERIALIZED response for the same reason the note tests do — a URL present
/// in JSON has already been handed out whatever the UI chooses to render.
/// </summary>
public class DocumentClassificationTests
{
    // ---- The class → default visibility matrix -----------------------------------------------------------

    [Theory]
    [InlineData(DocumentClass.PastMedicalHistory)]
    [InlineData(DocumentClass.MedicalReport)]
    [InlineData(DocumentClass.LabResult)]
    [InlineData(DocumentClass.Prescription)]
    [InlineData(DocumentClass.DischargeSummary)]
    [InlineData(DocumentClass.Referral)]
    public void Medical_material_defaults_to_Clinical(DocumentClass documentClass)
    {
        DocumentClassification.DefaultFor(documentClass).Should().Be(NoteVisibility.Clinical);
    }

    [Theory]
    [InlineData(DocumentClass.InvoiceReceipt)]
    [InlineData(DocumentClass.FinancialGuarantee)]
    public void Money_material_defaults_to_Financial(DocumentClass documentClass)
    {
        DocumentClassification.DefaultFor(documentClass).Should().Be(NoteVisibility.Financial);
    }

    [Theory]
    [InlineData(DocumentClass.PolicyContract)]
    [InlineData(DocumentClass.IdentityDocument)]
    [InlineData(DocumentClass.EnrolmentForm)]
    [InlineData(DocumentClass.MemberCorrespondence)]
    [InlineData(DocumentClass.Other)]
    public void Everything_else_defaults_to_Administrative(DocumentClass documentClass)
    {
        DocumentClassification.DefaultFor(documentClass).Should().Be(NoteVisibility.Administrative);
    }

    [Theory]
    [InlineData(SensitiveCategory.MentalHealth)]
    [InlineData(SensitiveCategory.HivSti)]
    [InlineData(SensitiveCategory.Genetic)]
    [InlineData(SensitiveCategory.SubstanceUse)]
    [InlineData(SensitiveCategory.Reproductive)]
    [InlineData(SensitiveCategory.Gbv)]
    public void A_declared_sensitive_category_forces_Restricted_whatever_the_class(SensitiveCategory category)
    {
        // The gap the build prompt leaves: "anything mental-health-related → Restricted" names no document
        // class, because those are properties of the CONTENT of a report. Without this the rule is
        // unimplementable and every such document defaults to merely Clinical — the exact material design 37
        // §6 exists to keep out of ordinary clinical reach.
        DocumentClassification.DefaultFor(DocumentClass.MedicalReport, category)
            .Should().Be(NoteVisibility.Restricted);
        // …and it beats an administrative class too, so filing it under a milder heading changes nothing.
        DocumentClassification.DefaultFor(DocumentClass.Other, category)
            .Should().Be(NoteVisibility.Restricted);
    }

    // ---- Raise, never lower ------------------------------------------------------------------------------

    [Fact]
    public void An_uploader_may_RAISE_the_visibility_above_the_class_default()
    {
        // An invoice that happens to name a diagnosis should be filed higher. Tightening is always allowed.
        DocumentClassification.Resolve(DocumentClass.InvoiceReceipt, null, NoteVisibility.Clinical)
            .Should().Be(NoteVisibility.Clinical);
    }

    [Theory]
    [InlineData(DocumentClass.PastMedicalHistory, NoteVisibility.Administrative)]
    [InlineData(DocumentClass.PastMedicalHistory, NoteVisibility.Financial)]
    [InlineData(DocumentClass.LabResult, NoteVisibility.Administrative)]
    [InlineData(DocumentClass.InvoiceReceipt, NoteVisibility.Administrative)]
    public void An_uploader_may_never_LOWER_it(DocumentClass documentClass, NoteVisibility attempted)
    {
        // Null is the refusal signal — the endpoint turns it into a 422 naming BOTH values. Silently applying
        // the floor instead would teach uploaders that the field does nothing.
        DocumentClassification.Resolve(documentClass, null, attempted).Should().BeNull();
    }

    [Fact]
    public void A_sensitive_category_cannot_be_talked_down_either()
    {
        DocumentClassification.Resolve(DocumentClass.MedicalReport, SensitiveCategory.HivSti, NoteVisibility.Clinical)
            .Should().BeNull("Restricted is the floor once a §5 category is declared");
    }

    [Fact]
    public void Omitting_the_visibility_takes_the_default()
    {
        DocumentClassification.Resolve(DocumentClass.LabResult, null, requested: null)
            .Should().Be(NoteVisibility.Clinical);
    }

    // ---- Download authority ------------------------------------------------------------------------------

    [Theory]
    [InlineData("finance")]
    [InlineData("claims_officer")]
    [InlineData("reception")]
    [InlineData("call_center")]
    public void Clinical_documents_are_not_downloadable_by_operational_roles(string role)
    {
        // The acceptance criterion, and the same hard rule notes carry: finance never receives a diagnosis,
        // and a scanned lab result is not an exception to it.
        DocumentAccess.MayDownload(NoteVisibility.Clinical, [role]).Should().BeFalse();
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("nurse")]
    [InlineData("medical_approval")]
    [InlineData("case_manager")]
    public void Clinical_documents_are_downloadable_by_clinical_roles(string role)
    {
        DocumentAccess.MayDownload(NoteVisibility.Clinical, [role]).Should().BeTrue();
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("medical_director")]
    [InlineData("super_admin")]
    public void Restricted_documents_are_existence_only_for_everyone_without_a_grant(string role)
    {
        // No ROLE reaches Restricted. Release runs through the design-37 §6 request/grant flow, and inventing
        // a parallel unlock here would be a side channel around the one mechanism that exists.
        DocumentAccess.MayDownload(NoteVisibility.Restricted, [role]).Should().BeFalse();
        DocumentAccess.MayDownload(NoteVisibility.Restricted, [role], hasSensitiveGrant: true).Should().BeTrue();
    }

    [Fact]
    public void An_unknown_role_downloads_nothing_above_administrative()
    {
        DocumentAccess.MayDownload(NoteVisibility.Clinical, ["some_new_role"]).Should().BeFalse();
        DocumentAccess.MayDownload(NoteVisibility.Financial, ["some_new_role"]).Should().BeFalse();
    }

    // ---- Upload authority is SEPARATE from download ------------------------------------------------------

    [Fact]
    public void A_finance_user_may_upload_an_invoice_but_not_a_past_medical_history()
    {
        // THE acceptance case. Clinical material entering the system with no clinical hand on it is both a
        // data-quality problem and a way to smuggle clinical content in under an administrative badge.
        DocumentAccess.MayUpload(DocumentClass.InvoiceReceipt, ["finance"]).Should().BeTrue();
        DocumentAccess.MayUpload(DocumentClass.PastMedicalHistory, ["finance"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("nurse")]
    [InlineData("beneficiary_mgmt")]
    public void Clinical_and_beneficiary_management_roles_may_file_clinical_material(string role)
    {
        DocumentAccess.MayUpload(DocumentClass.PastMedicalHistory, [role]).Should().BeTrue();
    }

    [Fact]
    public void Reception_may_attach_an_identity_document_but_no_clinical_one()
    {
        DocumentAccess.MayUpload(DocumentClass.IdentityDocument, ["reception"]).Should().BeTrue();
        DocumentAccess.MayUpload(DocumentClass.LabResult, ["reception"]).Should().BeFalse();
    }

    // ---- The registration classes ------------------------------------------------------------------------

    [Theory]
    [InlineData(DocumentClass.CardCopy)]
    [InlineData(DocumentClass.CaseDocument)]
    public void The_registration_classes_are_administrative(DocumentClass documentClass)
    {
        // A card scan and a case file are records ABOUT the administration of a membership, not clinical
        // material — so they sit where finance, claims and the call centre can read them, and anything
        // clinical inside a case belongs under MedicalReport, which carries the clinical floor.
        DocumentClassification.DefaultFor(documentClass).Should().Be(NoteVisibility.Administrative);
    }

    [Theory]
    [InlineData(DocumentClass.CardCopy)]
    [InlineData(DocumentClass.CaseDocument)]
    public void Beneficiary_management_may_file_the_registration_classes(DocumentClass documentClass)
    {
        DocumentAccess.MayUpload(documentClass, ["beneficiary_mgmt"]).Should().BeTrue();
        DocumentAccess.MayDownload(documentClass, NoteVisibility.Administrative, ["beneficiary_mgmt"]).Should().BeTrue();
    }

    [Fact]
    public void A_declared_sensitive_category_still_forces_restricted_on_a_case_document()
    {
        // The floor is per CLASS, but a declared 37 §5 category overrides it — otherwise GBV material could be
        // filed as a case document and read by everyone administrative.
        DocumentClassification.DefaultFor(DocumentClass.CaseDocument, SensitiveCategory.Gbv)
            .Should().Be(NoteVisibility.Restricted);
    }

    [Fact]
    public void The_photo_keeps_its_own_narrower_list_next_to_the_new_classes()
    {
        // Both are Administrative by visibility, but only one of them is a person's face. Finance may read a
        // card copy and must not read the photograph.
        DocumentAccess.MayDownload(DocumentClass.CardCopy, NoteVisibility.Administrative, ["finance"]).Should().BeTrue();
        DocumentAccess.MayDownload(DocumentClass.IdentityPhoto, NoteVisibility.Administrative, ["finance"]).Should().BeFalse();
    }

    // ---- The view: metadata only, and it says whether you may fetch --------------------------------------

    private static PolicyDocument Doc(
        NoteVisibility visibility = NoteVisibility.Clinical,
        DocumentClass documentClass = DocumentClass.DischargeSummary,
        DateOnly? documentDate = null, DateOnly? expiresOn = null) => new()
    {
        LinkId = Guid.NewGuid(), Scope = NoteScope.Member, ScopeRef = Guid.NewGuid(),
        DocumentId = Guid.NewGuid(), VersionNo = 1,
        DocumentClass = documentClass, VisibilityClass = visibility,
        Title = "Discharge summary — Kasr El Aini", DocumentDate = documentDate, ExpiresOn = expiresOn,
        UploadedByUserId = Guid.NewGuid(), UploadedByUsername = "officer.mona", UploadedByDisplay = "Mona Adel",
        UploadedAt = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
        Status = DocumentLinkStatus.Active,
    };

    [Fact]
    public void A_list_entry_never_carries_a_download_URL()
    {
        // A list that carried links would hand the content to everyone who can see the list — which is exactly
        // the wider audience the existence/content split exists to serve differently.
        var view = PolicyDocumentView.For(Doc(), ["doctor"], new DateOnly(2026, 7, 20));

        JsonSerializer.Serialize(view).Should().NotContain("http", "downloading is a separate, audited act");
    }

    [Fact]
    public void A_denied_caller_still_sees_the_document_EXISTS()
    {
        var view = PolicyDocumentView.For(Doc(), ["finance"], new DateOnly(2026, 7, 20));

        view.CanDownload.Should().BeFalse();
        view.DocumentClass.Should().Be("DischargeSummary");
        view.Title.Should().NotBeNullOrWhiteSpace();
        view.UploadedByUsername.Should().Be("officer.mona");
        view.UploadedAt.Offset.Should().Be(TimeSpan.Zero, "the API returns UTC; the UI renders Africa/Cairo");
    }

    [Fact]
    public void Expiry_is_projected_rather_than_left_for_the_client_to_compute()
    {
        var expired = PolicyDocumentView.For(
            Doc(expiresOn: new DateOnly(2026, 6, 30)), ["doctor"], new DateOnly(2026, 7, 20));
        var live = PolicyDocumentView.For(
            Doc(expiresOn: new DateOnly(2026, 12, 31)), ["doctor"], new DateOnly(2026, 7, 20));

        expired.Expired.Should().BeTrue();
        live.Expired.Should().BeFalse();
    }

    [Fact]
    public void Document_date_is_carried_separately_from_upload_date()
    {
        // Past medical history is read in CLINICAL order: a 2019 discharge summary scanned in today belongs in
        // 2019 on the member's history, not at the top. Both dates travel so the UI can show both.
        var view = PolicyDocumentView.For(
            Doc(documentDate: new DateOnly(2019, 4, 12)), ["doctor"], new DateOnly(2026, 7, 20));

        view.DocumentDate.Should().Be(new DateOnly(2019, 4, 12));
        view.UploadedAt.Year.Should().Be(2026);
    }

    [Fact]
    public void PHI_classes_are_flagged_so_a_download_audits_at_the_higher_severity()
    {
        Doc(NoteVisibility.Clinical).IsPhi.Should().BeTrue();
        Doc(NoteVisibility.Restricted).IsPhi.Should().BeTrue();
        Doc(NoteVisibility.Financial).IsPhi.Should().BeFalse();
        Doc(NoteVisibility.Administrative).IsPhi.Should().BeFalse();
    }
}
