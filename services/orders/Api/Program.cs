using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Orders.Api;
using Mersal.Orders.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("orders-service");
// Orders authorizes with the order overlay: treating-relationship on create/read (+ provider PO for phase-5 reads).
builder.Services.AddHbmpAuthorization(OrdersPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddOrdersInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<OrdersGate>();
builder.Services.AddScoped<FulfillmentGate>();

// Line codes validated against masterdata; treating relationship verified via emr-service (both fail-closed).
builder.Services.AddHttpClient<ICodeValidator, HttpCodeValidator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
builder.Services.AddHttpClient<ITreatingRelationshipClient, HttpTreatingRelationshipClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080"));
// Result reports are stored in document-service (scanned, CMK blob); we keep only the returned blob ref.
builder.Services.AddHttpClient<IReportDocumentClient, HttpReportDocumentClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Document:BaseUrl"] ?? "http://document-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("orders-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "orders-service" })).AllowAnonymous();

app.MapOrders();
app.MapQueue();      // phase 5.1 provider queue + search
app.MapConsume();    // phase 5.2 atomic idempotent consume
app.MapResults();    // phase 5.3 result upload + routing

app.Run();

public partial class Program;
