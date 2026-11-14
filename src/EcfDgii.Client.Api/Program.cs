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

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

try
{
    Log.Information("Starting web host");

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

    var pollingSection = builder.Configuration.GetSection("EcfStatusPolling");
    var pollingIntervalSeconds = pollingSection.GetValue<int?>("PollingIntervalSeconds")
        ?? (pollingSection.GetValue<int?>("PollingIntervalMinutes") * 60)
        ?? 15;
    var minDocumentAgeSeconds = pollingSection.GetValue<int?>("MinDocumentAgeSeconds")
        ?? (pollingSection.GetValue<int?>("MinDocumentAgeMinutes") * 60)
        ?? 10;
    var minUncertainAgeMinutes = pollingSection.GetValue("MinUncertainAgeMinutes", pollingSection.GetValue("MinDocumentAgeMinutes", 2));
    var pollingOptions = new EcfStatusPollingOptions(
        PollingInterval: TimeSpan.FromSeconds(Math.Max(5, pollingIntervalSeconds)),
        MinDocumentAge: TimeSpan.FromSeconds(Math.Max(1, minDocumentAgeSeconds)),
        MaxPollingWindow: TimeSpan.FromHours(pollingSection.GetValue("MaxPollingWindowHours", 72)),
        BatchSize: pollingSection.GetValue("BatchSize", 50),
        MinUncertainAge: TimeSpan.FromMinutes(Math.Max(1, minUncertainAgeMinutes)));
    builder.Services.AddSingleton(pollingOptions);
    builder.Services.AddHostedService<EcfStatusPollingBackgroundService>();

    builder.Services.AddOptions<EcfEmisorOptions>()
        .Bind(builder.Configuration.GetSection(EcfEmisorOptions.SectionName))
        .Validate(o => RncFormat.IsValid(o.Rnc),
            $"{EcfEmisorOptions.SectionName}:{nameof(EcfEmisorOptions.Rnc)} is required and must be a valid RNC (9 digits) or cédula (11 digits).")
        .Validate(o => !string.IsNullOrWhiteSpace(o.RazonSocial),
            $"{EcfEmisorOptions.SectionName}:{nameof(EcfEmisorOptions.RazonSocial)} is required.")
        .ValidateOnStart();

    builder.Services.AddSingleton<INonceCache>(sp =>
    {
        var memCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var redis = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
        return new MemoryNonceCache(memCache, redis);
    });
    builder.Services.AddScoped<IWorkerKeyResolver, ConfigurationWorkerKeyResolver>();
    builder.Services.AddSingleton<IIdempotencyStore, DbIdempotencyStore>();

    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructureServices(builder.Configuration);

    var jwtSettingsSection = builder.Configuration.GetSection("JwtSettings");
    var jwtSettings = jwtSettingsSection.Get<JwtSettings>();
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

    builder.Services.AddOpenApi();

    var healthChecksBuilder = builder.Services.AddHealthChecks()
        .AddDbContextCheck<ApplicationDbContext>();

    var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
    if (!string.IsNullOrEmpty(redisConnectionString))
    {
        healthChecksBuilder.AddRedis(redisConnectionString, name: "redis");
    }

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

    if (!app.Environment.IsDevelopment())
    {
        var workerSecret = app.Configuration["WORKER_SECRET_KEY"] ?? app.Configuration["WorkerSecretKey"];
        if (string.IsNullOrWhiteSpace(workerSecret) || workerSecret == "WorkerSecretKey" || workerSecret.Contains("Default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY FAIL-FAST: WorkerSecretKey is missing or configured with the insecure default value in a Non-Development environment.");
        }
    }

    if (!app.Environment.IsDevelopment())
    {
        var configuredJwtSecret = jwtSettings?.Secret;
        if (string.IsNullOrWhiteSpace(configuredJwtSecret) || configuredJwtSecret == "DefaultSecretKeyForTesting_MustBeAtLeast32Bytes!")
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY FAIL-FAST: JwtSettings:Secret is missing or configured with the insecure default value in a Non-Development environment.");
        }
    }

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
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            var now = DateTime.Now;
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

    app.UseMiddleware<GlobalExceptionMiddleware>();

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
        var httpsPort = app.Configuration["HTTPS_PORT"] ?? app.Configuration["ASPNETCORE_HTTPS_PORT"];
        if (!string.IsNullOrWhiteSpace(httpsPort))
        {
            app.UseHttpsRedirection();
        }
    }

    try
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (context.Database.IsRelational())
        {
            EnsureMigrationHistoryBaseline(context);
            try
            {
                context.Database.Migrate();
            }
            catch (Exception ex) when (IsRelationAlreadyExists(ex))
            {
                Log.Warning(ex, "Database objects already exist. Marking baseline migrations as reconciled.");
                EnsureMigrationHistoryBaseline(context);
                try
                {
                    context.Database.Migrate();
                }
                catch (Exception retryEx)
                {
                    Log.Warning(retryEx, "Second migration attempt after baseline reconciliation: {Message}", retryEx.Message);
                }
            }
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

static void EnsureMigrationHistoryBaseline(ApplicationDbContext context)
{
    try
    {
        context.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                ""MigrationId"" character varying(150) NOT NULL,
                ""ProductVersion"" character varying(32) NOT NULL,
                CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
            );

            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_name = 'ecf_documents'
                ) THEN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'ecf_documents' AND column_name = 'signed_rfce_content'
                    ) THEN
                        ALTER TABLE ecf_documents ADD COLUMN signed_rfce_content text;
                    END IF;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_name = 'customers'
                ) THEN
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260805214117_InitialCreate', '10.0.10')
                    ON CONFLICT (""MigrationId"") DO NOTHING;

                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260806123843_RotateSeededAdminPasswordHash', '10.0.10')
                    ON CONFLICT (""MigrationId"") DO NOTHING;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = 'ecf_documents' AND column_name = 'last_status_check_at'
                ) THEN
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260806141616_AddEcfStatusPollingColumns', '10.0.10')
                    ON CONFLICT (""MigrationId"") DO NOTHING;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = 'ecf_documents' AND column_name = 'ambiente'
                ) THEN
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20261005000000_AddAmbienteColumnAndUniqueIndexes', '10.0.10')
                    ON CONFLICT (""MigrationId"") DO NOTHING;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = 'ecf_documents' AND column_name = 'signed_rfce_content'
                ) THEN
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20261005010000_AddSignedRfceContentColumn', '10.0.10')
                    ON CONFLICT (""MigrationId"") DO NOTHING;

                    UPDATE ecf_documents
                    SET security_code = substring(signed_rfce_content from '<CodigoSeguridadeCF>([^<]+)</CodigoSeguridadeCF>')
                    WHERE signed_rfce_content LIKE '%<CodigoSeguridadeCF>%'
                      AND substring(signed_rfce_content from '<CodigoSeguridadeCF>([^<]+)</CodigoSeguridadeCF>') IS NOT NULL
                      AND (security_code IS NULL OR security_code != substring(signed_rfce_content from '<CodigoSeguridadeCF>([^<]+)</CodigoSeguridadeCF>'));
                END IF;
            END $$;
        ");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not run pre-migration baseline check: {Message}", ex.Message);
    }
}

static bool IsRelationAlreadyExists(Exception ex)
{
    for (var current = ex; current != null; current = current.InnerException!)
    {
        if (current is Npgsql.PostgresException pex && (pex.SqlState == "42P07" || pex.SqlState == "42701"))
        {
            return true;
        }
        if (current.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }
    return false;
}

public partial class Program { }
