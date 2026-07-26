using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Notification.Infrastructure;

/// <summary>Wires notification-service persistence, channels (live in-app + email; SMS/WhatsApp stubs flagged off),
/// the fan-out dispatcher, and the escalation/retry sweeps.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<NotificationDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Notification")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));

        var options = new NotificationOptions();
        config.GetSection(NotificationOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // Channels — live in-app + email; SMS/WhatsApp future-channel stubs (flagged OFF by default).
        services.AddScoped<INotificationChannel, InAppChannel>();
        services.AddScoped<INotificationChannel, EmailChannel>();
        services.AddScoped<INotificationChannel, SmsChannel>();
        services.AddScoped<INotificationChannel, WhatsAppChannel>();
        services.AddScoped<IEmailProvider, LoggingEmailProvider>();

        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<EscalationService>();
        return services;
    }
}
