namespace Mersal.MasterData.Api;

/// <summary>
/// 31.6 — the response shapes this service returns, written down.
///
/// <para>An anonymous object returned from an endpoint IS a contract; it was simply an unwritten one. A
/// minimal API returning <c>Results.Ok(new { … })</c> publishes no schema, so the OpenAPI drift gate compared
/// the route and the request and passed silently over the body — while the SPA parsed that body with a strict
/// zod schema and would have failed at runtime, on a clinical screen, with nothing failing in between.</para>
///
/// <para>Naming them changes no payload: every record here carries exactly the property names the anonymous
/// object carried, in the same casing. What changes is that the shape is now in
/// <c>docs/api/masterdata.json</c> and a change to it shows up as drift.</para>
/// </summary>

/// <summary>
/// An OP-Procedure kind, as the composer offers it.
/// </summary>
/// <param name="IsSessionBased">
/// Whether this kind is delivered as a COURSE. It decides whether the composer shows a session field at all —
/// and a session count on a type that is not session-based is a course nobody can deliver (design 45 §2).
/// </param>
/// <param name="DefaultSessions">Null for a type that is not session-based — a different fact from 1.</param>
/// <param name="AllowedCptScopes">
/// The CPT ranges this kind may accompany. Carried so the composer can refuse a mismatch as the doctor picks,
/// rather than after the write path answers 422.
/// </param>
public sealed record ProcedureTypeView(
    string Code,
    string NameEn,
    string NameAr,
    bool IsSessionBased,
    int? DefaultSessions,
    int? MaxSessions,
    IReadOnlyList<string> AllowedCptScopes,
    bool IsActive,
    int SortOrder);

/// <summary>A procedure type accepted against a CPT code — the composer's pre-flight answer.</summary>
/// <param name="Ok">Always true on this shape; a mismatch is a 422 problem, not a false in a 200.</param>
public sealed record ProcedureTypeValidationView(bool Ok, string Type, string? CptCode, string? Section);

/// <summary>Whether an id exists, answered as its own fact rather than as a 404.</summary>
/// <remarks>
/// A caller checking existence is asking a question, not fetching a thing; 404 would make "it is not there"
/// indistinguishable from "the route is wrong".
/// </remarks>
public sealed record ExistsView(Guid Id, bool Exists);

/// <summary>
/// A product's identity and the MOLECULES it resolves to.
/// </summary>
/// <param name="Name">
/// The name MASTER DATA holds, never one the client sent — the same rule the prescription-create path
/// enforces, and for the same reason: a client-supplied label would let the medicine named in a safety
/// warning differ from the drug actually prescribed.
/// </param>
/// <param name="IngredientKeys">
/// Empty rather than absent for a product with none recorded — 2,786 are in that state, and the duplicate
/// therapy check has to be able to say "nothing is recorded" rather than quietly omitting the line.
/// </param>
public sealed record DrugIngredientView(
    Guid DrugId,
    string? Name,
    string? ScientificName,
    string? AtcCode,
    IReadOnlyList<string> IngredientKeys);

/// <summary>Every id asked about is answered for — see <see cref="DrugIngredientView.IngredientKeys"/>.</summary>
public sealed record DrugIngredientsView(IReadOnlyList<DrugIngredientView> Items);
