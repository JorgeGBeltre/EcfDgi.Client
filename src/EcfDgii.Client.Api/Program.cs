using System;
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

    // Fail loudly at startup if this instance's emisor RNC is missing or malformed, instead of
    // DocumentsController silently defaulting to a placeholder RNC on every document it signs.
    builder.Services.AddOptions<EcfEmisorOptions>()
        .Bind(builder.Configuration.GetSection(EcfEmisorOptions.SectionName))
        .Validate(o => RncFormat.IsValid(o.Rnc),
            $"{EcfEmisorOptions.SectionName}:{nameof(EcfEmisorOptions.Rnc)} is required and must be a valid RNC (9 digits) or cédula (11 digits).")
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
    var key = Encoding.ASCII.GetBytes(jwtSettings?.Secret ?? "DefaultSecretKeyForTesting_MustBeAtLeast32Bytes!");

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

    // Fail-fast check for default worker secrets in Non-Development environments (Point #10)
    if (!app.Environment.IsDevelopment())
    {
        var workerSecrets = app.Configuration.GetSection("WorkerKeys").GetChildren();
        foreach (var sec in workerSecrets)
        {
            var secret = sec["Secret"];
            if (string.IsNullOrWhiteSpace(secret) || secret == "WorkerSecretKey" || secret.Contains("Default", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"CRITICAL SECURITY FAIL-FAST: WorkerKey '{sec["KeyId"]}' is configured with insecure default secret in Non-Development environment.");
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
