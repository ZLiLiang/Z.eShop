using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ServiceDefaults
{
    public static class Extensions
    {
        public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
        {
            // Enable Semantic Kernel OpenTelemetry
            AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

            builder.AddBasicServiceDefaults();

            builder.Services.AddServiceDiscovery();

            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                // Turn on resilience by default
                http.AddStandardResilienceHandler();

                // Turn on service discovery by default
                http.AddServiceDiscovery();
            });

            return builder;
        }

        /// <summary>
        /// Adds the services except for making outgoing HTTP calls.
        /// </summary>
        /// <remarks>
        /// This allows for things like Polly to be trimmed out of the app if it isn't used.
        /// </remarks>
        public static IHostApplicationBuilder AddBasicServiceDefaults(this IHostApplicationBuilder builder)
        {
            // Default health checks assume the event bus and self health checks
            builder.AddDefaultHealthChecks();

            builder.ConfigureOpenTelemetry();

            return builder;
        }

        public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();

                    // Configure Semantic Kernel telemetry
                    metrics.AddMeter("Microsoft.SemanticKernel*");
                })
                .WithTracing(tracing =>
                {
                    if (builder.Environment.IsDevelopment())
                    {
                        // We want to view all traces in development
                        tracing.SetSampler(new AlwaysOnSampler());
                    }

                    tracing.AddAspNetCoreInstrumentation()
                        .AddGrpcClientInstrumentation()
                        .AddHttpClientInstrumentation();

                    // Configure Semantic Kernel telemetry
                    tracing.AddSource("Microsoft.SemanticKernel*");
                });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
        {
            var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());
                builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter());
                builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
            }

            return builder;
        }

        public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
        {
            builder.Services.AddHealthChecks()
                // Add a default liveness check to ensure app is responsive
                .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

            return builder;
        }

        public static WebApplication MapDefaultEndpoints(this WebApplication app)
        {
            // Uncomment the following line to enable the Prometheus endpoint (requires the OpenTelemetry.Exporter.Prometheus.AspNetCore package)
            // app.MapPrometheusScrapingEndpoint();

            // Adding health checks endpoints to applications in non-development environments has security implications.
            // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
            if (app.Environment.IsDevelopment())
            {
                // All health checks must pass for app to be considered ready to accept traffic after starting
                app.MapHealthChecks("/health");

                // Only health checks tagged with the "live" tag must pass for app to be considered alive
                app.MapHealthChecks("/alive", new HealthCheckOptions
                {
                    Predicate = r => r.Tags.Contains("live")
                });
            }

            return app;
        }

        public static IApplicationBuilder UseDefaultOpenApi(this WebApplication app)
        {
            var configuration = app.Configuration;
            var openApiSection = configuration.GetSection("OpenApi");

            if (!openApiSection.Exists())
            {
                return app;
            }

            app.UseSwagger();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwaggerUI(setup =>
                {
                    /// {
                    ///   "OpenApi": {
                    ///     "Endpoint: {
                    ///         "Name": 
                    ///     },
                    ///     "Auth": {
                    ///         "ClientId": ..,
                    ///         "AppName": ..
                    ///     }
                    ///   }
                    /// }
                    var pathBase = configuration["PATH_BASE"] ?? string.Empty;
                    var authSection = openApiSection.GetSection("Auth");
                    var endpointSection = openApiSection.GetRequiredSection("Endpoint");

                    foreach (var description in app.DescribeApiVersions())
                    {
                        var name = description.GroupName;
                        var url = endpointSection["Url"] ?? $"{pathBase}/swagger/{name}/swagger.json";

                        setup.SwaggerEndpoint(url, name);
                    }

                    if (authSection.Exists())
                    {
                        setup.OAuthClientId(authSection.GetRequiredValue("ClientId"));
                        setup.OAuthAppName(authSection.GetRequiredValue("AppName"));
                    }
                });

                // Add a redirect from the root of the app to the swagger endpoint
                app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
            }

            return app;
        }

        public static IHostApplicationBuilder AddDefaultOpenApi(this IHostApplicationBuilder builder, IApiVersioningBuilder? apiVersioning = default)
        {
            var services = builder.Services;
            var configuration = builder.Configuration;
            var openApi = configuration.GetSection("OpenApi");

            if (!openApi.Exists())
            {
                return builder;
            }

            services.AddEndpointsApiExplorer();

            if (apiVersioning is not null)
            {
                // the default format will just be ApiVersion.ToString(); for example, 1.0.
                // this will format the version as "'v'major[.minor][-status]"
                apiVersioning.AddApiExplorer(options => options.GroupNameFormat = "'v'VVV");
                services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();
                services.AddSwaggerGen(options => options.OperationFilter<OpenApiDefaultValues>());
            }

            return builder;
        }
    }
}
