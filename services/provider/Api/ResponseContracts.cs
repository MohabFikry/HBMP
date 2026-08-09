namespace Mersal.Provider.Api;

/// <summary>
/// 31.6 — response shapes this service returns that had no name.
///
/// <para>An anonymous object returned from an endpoint IS a contract; it was simply unwritten, so the OpenAPI
/// drift gate compared the route and the request and passed over the body. The property names and casing here
/// are exactly what the anonymous objects carried, so the JSON is byte-identical.</para>
/// </summary>

/// <summary>A clinical specialty, as the practitioner forms offer it.</summary>
/// <param name="ParentCode">Null at the top of the tree. A specialty's parent is what makes "surgery"
/// answerable when a branch is contracted for "general surgery".</param>
public sealed record SpecialtyView(string SpecialtyCode, string NameEn, string? NameAr, string? ParentCode);
