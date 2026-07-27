using Mersal.Profile.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Profile.Infrastructure;

/// <summary>
/// Wires the composition engine and its section providers.
///
/// <para><b>What is NOT here is the point.</b> There is no <c>AddClientCredentials</c>, no token cache, no
/// service-account handler on any of these clients. Every sibling client is a bare <see cref="HttpClient"/> with
/// a base address; the only Authorization header any of them ever carries is the one
/// <see cref="CallerScopedHttp"/> copies from the incoming request. The architecture test in this service's test
/// project fails the build if that stops being true.</para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The sibling services the profile composes from, and their default in-cluster addresses.</summary>
    public static IReadOnlyList<string> Siblings { get; } =
        ["policy", "emr", "orders", "pharmacy", "approvals", "claims", "case", "callcentre", "document"];

    public static IServiceCollection AddProfileComposition(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var name in Siblings)
        {
            var url = configuration[$"Siblings:{Capitalize(name)}"] ?? $"http://{name}-service:8080";
            services.AddHttpClient(name, c =>
            {
                c.BaseAddress = new Uri(url);
                // Belt and braces on top of the per-section CancellationToken: a socket that never answers must
                // not hold a section open past the composer's budget.
                c.Timeout = TimeSpan.FromSeconds(5);
            });
        }

        services.AddScoped<CallerScopedHttp>();

        // Scoped, so the memo inside each shared source lives exactly one request — one caller, one patient, one
        // authorization context. Phase 18's X9 lesson: never key a cache on fewer dimensions than the decision.
        services.AddScoped<AdministrativeSource>();
        services.AddScoped<ClinicalContextSource>();

        services.AddScoped<IProfileFactResolver, HttpProfileFactResolver>();
        services.AddScoped<ICallVerificationGate, HttpCallVerificationGate>();

        services.AddScoped<ISectionProvider, HeaderSectionProvider>();
        services.AddScoped<ISectionProvider, AlertsSectionProvider>();
        services.AddScoped<ISectionProvider, CoverageSectionProvider>();
        services.AddScoped<ISectionProvider, PastMedicalHistoryProvider>();
        services.AddScoped<ISectionProvider, EncountersSectionProvider>();
        services.AddScoped<ISectionProvider, InvestigationsSectionProvider>();
        services.AddScoped<ISectionProvider, PrescriptionsSectionProvider>();
        services.AddScoped<ISectionProvider, AuthorizationsSectionProvider>();
        services.AddScoped<ISectionProvider, ReferralsSectionProvider>();
        services.AddScoped<ISectionProvider, DocumentsSectionProvider>();
        services.AddScoped<ISectionProvider, NotesSectionProvider>();
        services.AddScoped<ISectionProvider, FinancialSectionProvider>();
        services.AddScoped<ISectionProvider, CaseManagementSectionProvider>();
        services.AddScoped<ISectionProvider, TimelineSectionProvider>();
        services.AddScoped<ISectionProvider, CallHistorySectionProvider>();

        services.AddSingleton(new ProfileCompositionOptions());
        services.AddScoped<ProfileComposer>();

        return services;
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
