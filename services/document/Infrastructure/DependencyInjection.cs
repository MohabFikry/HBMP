using Mersal.Document.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Document.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<DocumentDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Document") ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
             .UseSnakeCaseNamingConvention());

        services.Configure<ClamAvOptions>(config.GetSection(ClamAvOptions.SectionName));
        services.Configure<BlobStoreOptions>(config.GetSection(BlobStoreOptions.SectionName));

        services.AddSingleton<IMalwareScanner, ClamAvScanner>();
        services.AddSingleton<IBlobStore, MinioBlobStore>();
        services.AddScoped<UploadValidator>();
        services.AddScoped<DocumentUploadService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
