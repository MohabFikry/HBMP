namespace Mersal.ClinicalValidation;

/// <summary>
/// The result of fetching the data a clinical check needs: either the data, with its provenance, or an
/// explicit statement that it could not be obtained.
/// </summary>
/// <typeparam name="T">The fetched payload.</typeparam>
/// <remarks>
/// <para>
/// This type exists to make one specific bug unrepresentable. Before phase 26,
/// <c>pharmacy/Api/HttpClients.cs</c> caught every transport error and returned "no alerts", and treated any
/// non-2xx response the same way — so an outage, a 500 or a 403 all rendered to the prescriber as a clean
/// bill of health. Doc 43 §1 calls that the most dangerous line in the prescribing path.
/// </para>
/// <para>
/// Deleting those catches would fix the instances. This fixes the <i>class</i>: an
/// <see cref="Unavailable"/> carries no payload, so there is no empty collection for a checker to inspect
/// and conclude "nothing found". The only thing a checker can do with an unavailable source is report it.
/// There is no code path from a failed fetch to <see cref="CheckState.Ok"/>.
/// </para>
/// </remarks>
public abstract record Fetched<T>
{
    private Fetched() { }

    /// <summary>The data, with the provenance every advisory is required to carry (doc 43 §1 rule 2).</summary>
    public sealed record Available(T Value, ProvenanceInfo Provenance) : Fetched<T>;

    /// <summary>
    /// The data could not be obtained. Carries a reason for display, and <b>no payload</b> — that absence is
    /// the point of the type.
    /// </summary>
    public sealed record Unavailable(string Reason) : Fetched<T>;
}

/// <summary>Factories for <see cref="Fetched{T}"/>, kept off the generic type itself (CA1000).</summary>
public static class Fetched
{
    /// <summary>Data obtained, with the provenance the finding will carry.</summary>
    public static Fetched<T> From<T>(T value, ProvenanceInfo provenance) =>
        new Fetched<T>.Available(value, provenance);

    /// <summary>
    /// The source could not be reached. Note that there is no value to supply — that is deliberate, and it
    /// is what stops a caller from "defaulting" a failed fetch to an empty result.
    /// </summary>
    public static Fetched<T> NotAvailable<T>(string reason) => new Fetched<T>.Unavailable(reason);
}

/// <summary>
/// Where an advisory came from and when — shown to the prescriber and stored with the prescription.
/// </summary>
/// <remarks>
/// Doc 43 §1: "a warning you cannot attribute is a warning a clinician is right to ignore". For the
/// indication check this is not a formality — the mapping is generated at ATC level 4 and is, in its own
/// author's words, clinical judgement rather than a published dataset, which is exactly what a prescriber
/// needs to know to weigh it.
/// </remarks>
/// <param name="SourceName">Human-readable source, e.g. "Mersal interaction list".</param>
/// <param name="SourceVersion">The dataset release the answer came from.</param>
/// <param name="RetrievedAt">When the data was obtained.</param>
/// <param name="Caveat">
/// An optional plain statement of the source's limits, displayed with the finding. Coverage that is known to
/// be partial has to say so; "checked" implying "complete" is how a partial list becomes a false assurance.
/// </param>
public sealed record ProvenanceInfo(
    string SourceName,
    string SourceVersion,
    DateTimeOffset RetrievedAt,
    string? Caveat = null);
