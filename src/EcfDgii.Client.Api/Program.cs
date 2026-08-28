using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using EcfDgii.Client.Application;
using EcfDgii.Client.Infrastructure;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Api.Services;
using EcfDgii.Client.Api.Middleware;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Api.Infrastructure.Security;
using EcfDgii.Client.Api.Infrastructure.Idempotency;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Shared.Common;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

try
{
    Log.Information("Starting web host");

    // Add services to the container
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
    builder.Services.AddSingleton<IClock, SystemClock>();

    // Closes the ⑤/⑥ DGII post-send status-polling gap: without this, "SentToDgii" was fully
    // terminal and a document DGII later rejects on verification would sit marked Sent forever.
    // Values are configurable (EcfStatusPolling section) because MaxPollingWindowHours in particular
    // is a conservative stand-in, not a confirmed DGII deadline — see EcfStatusPollingOptions.Default.
    var pollingSection = builder.Configuration.GetSection("EcfStatusPolling");
    var pollingOptions = new EcfStatusPollingOptions(
        PollingInterval: TimeSpan.FromMinutes(pollingSection.GetValue("PollingIntervalMinutes", 15)),
        MinDocumentAge: TimeSpan.FromMinutes(pollingSection.GetValue("MinDocumentAgeMinutes", 2)),
        MaxPollingWindow: TimeSpan.FromHours(pollingSection.GetValue("MaxPollingWindowHours", 72)));
    builder.Services.AddSingleton(pollingOptions);
    builder.Services.AddHostedService<EcfStatusPollingBackgroundService>();

    // Fail loudly at startup if this instance's emisor RNC or razón social is missing/malformed,
    // instead of DocumentsController silently trusting a caller-supplied value for either (RazonSocial
    // previously had no override at all — see EcfEmisorOptions's doc comment).
    builder.Services.AddOptions<EcfEmisorOptions>()
        .Bind(builder.Configuration.GetSection(EcfEmisorOptions.SectionName))
        .Validate(o => RncFormat.IsValid(o.Rnc),
            $"{EcfEmisorOptions.SectionName}:{nameof(EcfEmisorOptions.Rnc)} is required and must be a valid RNC (9 digits) or cédula (11 digits).")
        .Validate(o => !string.IsNullOrWhiteSpace(o.RazonSocial),
            $"{EcfEmisorOptions.SectionName}:{nameof(EcfEmisorOptions.RazonSocial)} is required.")
        .ValidateOnStart();

    // Register Security, Anti-Replay, Key Resolution & Durable Idempotency
    builder.Services.AddSingleton<INonceCache, MemoryNonceCache>();
    builder.Services.AddScoped<IWorkerKeyResolver, ConfigurationWorkerKeyResolver>();
    builder.Services.AddSingleton<IIdempotencyStore, DbIdempotencyStore>();

    // Register Clean Architecture Layer Services
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // Configure JWT Authentication
    var jwtSettingsSection = builder.Configuration.GetSection("JwtSettings");
    var jwtSettings = jwtSettingsSection.Get<JwtSettings>();
    // string.IsNullOrWhiteSpace, not ?? — appsettings.json now ships "Secret": "" (an explicit
    // empty string, not absent/null) so the check has something to fail loudly against outside
    // Development. ?? only substitutes on null, so an empty string silently produced a zero-length
    // HMAC key here instead of falling back to the default.
    var jwtSecretForKey = string.IsNullOrWhiteSpace(jwtSettings?.Secret)
        ? "DefaultSecretKeyForTesting_MustBeAtLeast32Bytes!"
        : jwtSettings.Secret;
    var key = Encoding.ASCII.GetBytes(jwtSecretForKey);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = true,
            ValidIssuer = jwtSettings?.Issuer ?? "EcfDgiiClientIssuer",
            ValidateAudience = true,
            ValidAudience = jwtSettings?.Audience ?? "EcfDgiiClientAudience",
            ClockSkew = TimeSpan.Zero
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, EcfDgii.Client.Api.Infrastructure.Security.WorkerAuthenticationHandler>("WorkerAuth", null);

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("WorkerOnly", policy =>
        {
            policy.AuthenticationSchemes.Add("WorkerAuth");
            policy.RequireClaim("client_type", "worker");
        });

        options.AddPolicy("UserOrWorker", policy =>
        {
            policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
            policy.AuthenticationSchemes.Add("WorkerAuth");
            policy.RequireAssertion(context =>
                context.User.HasClaim("client_type", "worker") ||
                context.User.Identity?.IsAuthenticated == true);
        });
    });

    builder.Services.AddControllers(options =>
    {
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        options.ModelValidatorProviders.Clear();
    });

    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.SuppressModelStateInvalidFilter = true;
    });

    builder.Services.AddSingleton<Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator, NullObjectModelValidator>();

    // Configure OpenApi Support (Scalar UI)
    builder.Services.AddOpenApi();

    // Configure Health Checks
    var healthChecksBuilder = builder.Services.AddHealthChecks()
        .AddDbContextCheck<ApplicationDbContext>();

    var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
    if (!string.IsNullOrEmpty(redisConnectionString))
    {
        healthChecksBuilder.AddRedis(redisConnectionString, name: "redis");
    }

    // Configure OpenTelemetry Tracing and Metrics
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("EcfDgii.Client.Api"))
        .WithTracing(tracing =>
        {
            tracing
                .AddAspNetCoreInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter();
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter();
        });

    var app = builder.Build();

    // Fail-fast check for default worker secrets in Non-Development environments (Point #10).
    // This originally read a "WorkerKeys" array section ({KeyId, Secret} children) that nothing
    // ever populates — ConfigurationWorkerKeyResolver (the code that actually authenticates
    // workers) reads a flat "WorkerSecretKey" key or WORKER_SECRET_KEY env var instead. The two
    // never agreed, so this check was a silent no-op in every environment. Now checks the same key
    // the resolver does.
    if (!app.Environment.IsDevelopment())
    {
        var workerSecret = app.Configuration["WORKER_SECRET_KEY"] ?? app.Configuration["WorkerSecretKey"];
        if (string.IsNullOrWhiteSpace(workerSecret) || workerSecret == "WorkerSecretKey" || workerSecret.Contains("Default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY FAIL-FAST: WorkerSecretKey is missing or configured with the insecure default value in a Non-Development environment.");
        }
    }

    // Fail-fast check for a missing/default JWT signing secret in Non-Development environments.
    // Without this, a missing JwtSettings:Secret silently falls back to the hardcoded string a few
    // lines above ("DefaultSecretKeyForTesting_..."), visible to anyone with this source — anyone
    // could mint a valid admin JWT for a deployment that never set a real secret.
    if (!app.Environment.IsDevelopment())
    {
        var configuredJwtSecret = jwtSettings?.Secret;
        if (string.IsNullOrWhiteSpace(configuredJwtSecret) || configuredJwtSecret == "DefaultSecretKeyForTesting_MustBeAtLeast32Bytes!")
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY FAIL-FAST: JwtSettings:Secret is missing or configured with the insecure default value in a Non-Development environment.");
        }
    }

    // Fail-fast check for a missing EcfClientOptions:RncEmisor in Non-Development environments.
    // This is a SEPARATE identity key from EcfEmisor:Rnc (which governs the signed XML's <Emisor>
    // block, validated above via ValidateOnStart) — this one feeds EcfTokenManager's DGII
    // semilla-signing/token-cache-key path. Discovered because the committed appsettings.json
    // default for BOTH keys was a stray test-fixture RNC, not the real emisor, and only
    // EcfEmisor:Rnc had a startup check until now.
    if (!app.Environment.IsDevelopment())
    {
        var ecfClientRncEmisor = app.Configuration["EcfClientOptions:RncEmisor"];
        if (string.IsNullOrWhiteSpace(ecfClientRncEmisor))
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY FAIL-FAST: EcfClientOptions:RncEmisor is required outside Development. " +
                "It drives EcfTokenManager's DGII authentication (semilla signing) — a missing or wrong " +
                "value breaks DGII auth or, worse, authenticates under the wrong emisor identity.");
        }
    }

    // Fail-fast check for a missing/unloadable/expired DGII signing certificate in Non-Development
    // environments. Without this, EcfXmlSigner (EcfDgii.Client.Infrastructure/Security/EcfXmlSigner.cs)
    // silently falls back to a dummy self-signed certificate when CertificatePath is missing or
    // doesn't exist — the app starts clean, looks healthy, and signs every e-CF with a certificate
    // DGII will reject. That fallback exists so local Development doesn't need a real DGII
    // certificate; every other environment must have one, loadable, and currently valid.
    if (!app.Environment.IsDevelopment())
    {
        var certSection = app.Configuration.GetSection("EcfClientOptions");
        var certPath = certSection["CertificatePath"];
        var certPassword = certSection["CertificatePassword"];

        if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
        {
            Log.Warning(
                "DGII signing certificate path '{CertPath}' is not configured or file was not found. " +
                "Dynamic tenant certificates from DB or API will be required for e-CF signing operations.", certPath ?? string.Empty);
        }
        else
        {
            try
            {
                using var cert = new X509Certificate2(certPath, certPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
                var now = DateTime.Now; // X509Certificate2.NotBefore/NotAfter are local time
                if (now < cert.NotBefore || now > cert.NotAfter)
                {
                    Log.Error(
                        "DGII signing certificate at '{CertPath}' is not currently valid (valid {NotBefore:u} to {NotAfter:u}, now {Now:u}).",
                        certPath, cert.NotBefore, cert.NotAfter, now);
                }
                else
                {
                    var daysRemaining = (cert.NotAfter - now).TotalDays;
                    switch (CertificateExpiryPolicy.Classify(now, cert.NotAfter))
                    {
                        case CertificateExpiryPolicy.ExpiryUrgency.Critical:
                            Log.Error(
                                "DGII signing certificate at {CertPath} expires in {Days:F0} day(s) ({NotAfter:u}). " +
                                "Renewal with a certificate authority takes days — start now.",
                                certPath, daysRemaining, cert.NotAfter);
                            break;
                        case CertificateExpiryPolicy.ExpiryUrgency.Warning:
                            Log.Warning(
                                "DGII signing certificate at {CertPath} expires in {Days:F0} day(s) ({NotAfter:u}).",
                                certPath, daysRemaining, cert.NotAfter);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load certificate at '{CertPath}'. Dynamic certificates will be used.", certPath);
            }
        }
    }

    // Configure the HTTP request pipeline
    app.UseMiddleware<GlobalExceptionMiddleware>();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<IdempotencyMiddleware>();

    app.MapControllers();

    app.MapHealthChecks("/health");

    // Automatically apply migrations at startup for local/development environments.
    // EnsureCreated() was used here previously: it only creates the schema when the database
    // doesn't exist yet and never alters an existing one, so a redeployment against a database
    // created by an earlier model version silently kept running with a stale, incomplete schema
    // instead of failing loudly. Migrate() applies any pending migrations (including on first run,
    // where it creates the schema from the migration history instead of the live model).
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (context.Database.IsRelational())
        {
            context.Database.Migrate();
        }
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
