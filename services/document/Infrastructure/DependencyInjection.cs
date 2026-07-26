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
            o.UseNpgsql(config.GetConnectionString("Document") ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
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
