using System.Globalization;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

internal static class FakebookServiceDefaults
{
    public static IServiceCollection AddFakebookServiceDefaults(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var ratio = double.TryParse(configuration["Observability:TraceSampleRatio"] ?? configuration["OTEL_TRACES_SAMPLER_ARG"], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 0d, 1d) : 0.1d;
        var endpoint = Uri.TryCreate(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? configuration["OpenTelemetry:OtlpEndpoint"], UriKind.Absolute, out var uri) ? uri : null;
        var telemetry = services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: typeof(FakebookServiceDefaults).Assembly.GetName().Version?.ToString(), serviceInstanceId: Environment.MachineName));
        telemetry.WithTracing(tracing => { tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))).AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health")).AddHttpClientInstrumentation(); if (endpoint is not null) tracing.AddOtlpExporter(options => options.Endpoint = endpoint); });
        telemetry.WithMetrics(metrics => { metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation(); if (endpoint is not null) metrics.AddOtlpExporter(options => options.Endpoint = endpoint); });
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods()));
        return services;
    }
}
