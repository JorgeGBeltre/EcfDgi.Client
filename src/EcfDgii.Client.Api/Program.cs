using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
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
using System.Globalization;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Shared.Common;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

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
    builder.Services.AddMemoryCache(options =>
    {
        options.SizeLimit = 100_000;
    });
    builder.Services.AddResponseCompression();
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var key = httpContext.User.FindFirst("worker_key_id")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            });
        });
    });
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
    builder.Services.AddSingleton<IClock, SystemClock>();

    // Closes the ⑤/⑥ DGII post-send status-polling gap: without this, "SentToDgii" was fully
    // terminal and a document DGII later rejects on verification would sit marked Sent forever.
    // Values are configurable (EcfStatusPolling section) because MaxPollingWindowHours in particular
    // is a conservative stand-in, not a confirmed DGII deadline — see EcfStatusPollingOptions.Default.
    var pollingSection = builder.Configuration.GetSection("EcfStatusPolling");
    var pollingIntervalSeconds = pollingSection.GetValue<int?>("PollingIntervalSeconds")
        ?? (pollingSection.GetValue<int?>("PollingIntervalMinutes") * 60)
        ?? 60;
    var pollingOptions = new EcfStatusPollingOptions(
        PollingInterval: TimeSpan.FromSeconds(Math.Max(5, pollingIntervalSeconds)),
        MinDocumentAge: TimeSpan.FromMinutes(pollingSection.GetValue("MinDocumentAgeMinutes", 2)),
        MaxPollingWindow: TimeSpan.FromHours(pollingSection.GetValue("MaxPollingWindowHours", 72)),
        BatchSize: pollingSection.GetValue("BatchSize", 50));
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
    builder.Services.AddSingleton<INonceCache>(sp =>
    {
        var memCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var redis = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
        return new MemoryNonceCache(memCache, redis);
    });
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
    var jwtSecretForKey = builder.Configuration["JwtSettings:Secret"]
        ?? builder.Configuration["JWT_SECRET"]
        ?? builder.Configuration["Jwt:SecretKey"]
        ?? jwtSettings?.Secret;
    if (string.IsNullOrWhiteSpace(jwtSecretForKey))
    {
        jwtSecretForKey = "DefaultSecretKeyForTesting_MustBeAtLeast32Bytes!";
    }
    var key = Encoding.UTF8.GetBytes(jwtSecretForKey);

    var validIssuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "EcfDgiiClientIssuer",
        "Ecf.API"
    };
    if (!string.IsNullOrWhiteSpace(jwtSettings?.Issuer)) validIssuers.Add(jwtSettings.Issuer);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["JWT_ISSUER"])) validIssuers.Add(builder.Configuration["JWT_ISSUER"]!);

    var validAudiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "EcfDgiiClientAudience",
        "Ecf.Clients",
        "EcfDgiiClient"
    };
    if (!string.IsNullOrWhiteSpace(jwtSettings?.Audience)) validAudiences.Add(jwtSettings.Audience);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["JWT_AUDIENCE"])) validAudiences.Add(builder.Configuration["JWT_AUDIENCE"]!);

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
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidAudiences = validAudiences,
            ClockSkew = TimeSpan.FromMinutes(5)
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
                (context.User.Identity?.IsAuthenticated == true &&
                 (context.User.IsInRole("Admin") ||
                  context.User.IsInRole("FiscalOperator") ||
                  context.User.IsInRole("SuperAdmin") ||
                  context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "Admin") ||
                  context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "FiscalOperator") ||
                  context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "SuperAdmin") ||
                  context.User.HasClaim("role", "Admin") ||
                  context.User.HasClaim("role", "FiscalOperator") ||
                  context.User.HasClaim("role", "SuperAdmin"))));
        });
    });

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, token) =>
        {
            context.HttpContext.Response.ContentType = "application/problem+json";
            await context.HttpContext.Response.WriteAsync("{\"title\":\"Too Many Requests\",\"status\":429,\"detail\":\"Límite de tasa de solicitudes excedido.\"}", token);
        };
        options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var partitionKey = httpContext.User.FindFirst("worker_key_id")?.Value
                ?? httpContext.User.FindFirst("tenant_id")?.Value
                ?? httpContext.User.FindFirst("TenantId")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous";

            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 20,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });
        });
        options.AddPolicy("FiscalEmissionLimiter", httpContext =>
        {
            var partitionKey = httpContext.User.FindFirst("worker_key_id")?.Value
                ?? httpContext.User.FindFirst("tenant_id")?.Value
                ?? httpContext.User.FindFirst("TenantId")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous";

            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 10,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });
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
            throw new InvalidOperationException(
                $"CRITICAL SECURITY FAIL-FAST: DGII signing certificate path EcfClientOptions:CertificatePath ('{certPath}') does not exist. " +
                "A real DGII signing certificate is required outside Development.");
        }

        try
        {
            using var cert = new X509Certificate2(certPath, certPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
            var now = DateTime.Now; // X509Certificate2.NotBefore/NotAfter are local time
            if (now < cert.NotBefore || now > cert.NotAfter)
            {
                throw new InvalidOperationException(
                    $"CRITICAL SECURITY FAIL-FAST: DGII signing certificate at '{certPath}' is not currently valid " +
                    $"(valid {cert.NotBefore:u} to {cert.NotAfter:u}, now {now:u}).");
            }

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
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"CRITICAL SECURITY FAIL-FAST: DGII signing certificate at '{certPath}' could not be loaded " +
                "(wrong password or corrupt file).", ex);
        }
    }

    // Configure the HTTP request pipeline
    app.UseMiddleware<GlobalExceptionMiddleware>();

    // MED-142: Forwarded headers for reverse proxy (Traefik / Nginx) to reflect client IP and HTTPS scheme
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
    };
    forwardedHeadersOptions.KnownIPNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeadersOptions);

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    if (!app.Environment.IsDevelopment())
    {
        // HTTPS redirection only when port is configured or not behind TLS terminating reverse proxy
        var httpsPort = app.Configuration["HTTPS_PORT"] ?? app.Configuration["ASPNETCORE_HTTPS_PORT"];
        if (!string.IsNullOrWhiteSpace(httpsPort))
        {
            app.UseHttpsRedirection();
        }
    }

    // Automatically apply migrations at startup for relational databases before serving traffic.
    // MED-127: Migrate before mapping endpoints and fail fast on migration failure without swallowing.
    try
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (context.Database.IsRelational())
        {
            context.Database.Migrate();
        }
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Failed to apply database migrations on startup: {Message}", ex.Message);
        throw;
    }

    app.UseResponseCompression();
    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<IdempotencyMiddleware>();

    app.MapControllers();

    app.MapHealthChecks("/health");

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
