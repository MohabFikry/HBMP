using Mersal.Data;
using Mersal.Interop.Domain.Integration;
using Mersal.Interop.Infrastructure.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Interop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInteropInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // 18.B2 (audit R2 S2) — interop's policy was the only fail-OPEN one in the repo AND its GUC was never
        // bound, so the escape hatch was the only reason writes worked. 0003 closes the policy; this binds the
        // GUC. Both halves are required: closing the policy without the binder denies every row.
        services.AddHbmpRls();
        services.AddDbContext<InteropDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Interop")
                        ?? throw new System.InvalidOperationException(
                            "Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        services.AddScoped<IFhirDataSource, HttpFhirDataSource>();

        // 13.2 — integration-readiness layer: registry (DPIA-gated), ingestion (anti-corruption boundary),
        // inbound/outbound adapters (one real referral ACL + roadmap stubs), and the OCR/Arabic-NLP no-op hooks.
        services.AddScoped<IExternalPartnerRegistry, DbExternalPartnerRegistry>();
        services.AddScoped<InboundIngestionService>();

        services.AddScoped<IInboundIntegrationAdapter, ReferralNetworkAdapter>();
        services.AddScoped<IInboundIntegrationAdapter, UnhcrIdentifierAdapter>();
        services.AddScoped<IInboundIntegrationAdapter, GovernmentClaimAdapter>();
        services.AddScoped<IInboundIntegrationAdapter, InsurerEligibilityAdapter>();
        services.AddScoped<IInboundIntegrationAdapter, Hl7v2ReferralAdapter>();
        services.AddScoped<IOutboundIntegrationAdapter, ReferralNetworkAdapter>();

        services.AddSingleton<IDocumentOcrProvider, NoOpDocumentOcrProvider>();
        services.AddSingleton<IArabicNlpExtractor, NoOpArabicNlpExtractor>();
        return services;
    }
}
