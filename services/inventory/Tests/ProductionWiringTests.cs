using FluentAssertions;
using Mersal.Authz;
using Mersal.Inventory.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mersal.Inventory.Tests;

/// <summary>
/// The container can build the services this app actually ships with.
/// </summary>
/// <remarks>
/// <para><b>The defect, and why sixty-eight passing tests could not see it.</b> <c>HttpBranchDirectory</c>
/// caches a caller's permitted branch set and therefore takes <c>IMemoryCache</c>, and
/// <c>builder.Services.AddMemoryCache()</c> was never called. That is not a slower path; it is a dead one —
/// the typed client cannot be activated at all, so every inventory request threw at DI time and answered
/// <b>500</b>. The screen said "the service couldn't complete this request" and meant it literally.</para>
///
/// <para><see cref="InventoryApiFactory"/> could not catch it, by construction: it replaces
/// <c>IBranchDirectory</c> with a fake so the reach RULES can be tested without admin-service. That is the
/// right substitution for those tests, and it means the real implementation — the one with the missing
/// dependency — is never constructed anywhere in the suite. A test factory that removes the real
/// implementation of a seam can never find a wiring fault in it.</para>
///
/// <para>So this one boots the app with the same settings and <b>none of the substitutions</b>, and asks the
/// container to construct the things the fakes usually stand in for. It makes no HTTP call and needs no
/// database: the fault is in the object graph, and that is what it reads.</para>
/// </remarks>
public class ProductionWiringTests
{
    private sealed class RealWiring : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            // Enough configuration for Program to finish; deliberately no ConfigureTestServices, because the
            // point is the graph as deployed. DbContext construction is lazy, so a blank connection string is
            // fine — nothing here opens one.
            builder.UseSetting("ConnectionStrings:Inventory", "Host=localhost;Database=none");
            builder.UseSetting("Auth:Authority", "https://identity.test");
            builder.UseSetting("Auth:Audience", "hbmp");
            builder.UseSetting("Events:UseInMemoryOutbox", "true");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        }
    }

    [Fact]
    public void The_real_branch_directory_can_be_constructed()
    {
        using var app = new RealWiring();
        using var scope = app.Services.CreateScope();

        // This is the resolve that was throwing on every request:
        //   "Unable to resolve service for type 'IMemoryCache' while attempting to activate
        //    'Mersal.Inventory.Api.HttpBranchDirectory'."
        var directory = scope.ServiceProvider.GetRequiredService<IBranchDirectory>();

        directory.Should().NotBeNull();
        directory.GetType().Name.Should().Be("HttpBranchDirectory",
            "the fake belongs to InventoryApiFactory — a wiring test that resolved a substitute would prove nothing");
    }

    [Fact]
    public void The_real_medicines_directory_can_be_constructed()
    {
        using var app = new RealWiring();
        using var scope = app.Services.CreateScope();

        // The other typed client the suite fakes, and therefore the other one nothing was constructing. It
        // gates the catalogue against admitting medicines (D5), so a container that cannot build it fails
        // every item creation with a 500 rather than a decision.
        var medicines = scope.ServiceProvider.GetRequiredService<IMedicinesDirectory>();

        medicines.Should().NotBeNull();
        medicines.GetType().Name.Should().Be("HttpMedicinesDirectory");
    }
}
