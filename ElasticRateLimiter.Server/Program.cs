using ElasticRateLimiter.Core.Diagnostics;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(res => res.AddService("es-rate-limiter"))
    .WithTracing(tracing => tracing
        .AddSource(RateLimiterDiagnostics.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(RateLimiterDiagnostics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        .AddOtlpExporter());

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
