using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

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
    });

    builder.Services.AddAuthorization();

    builder.Services.AddControllers();

    // Configure OpenApi Support (Scalar UI)
    builder.Services.AddOpenApi();

    // Configure Health Checks
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<ApplicationDbContext>();

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

    // Configure the HTTP request pipeline
    app.UseMiddleware<GlobalExceptionMiddleware>();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.MapHealthChecks("/health");

    // Automatically apply migrations at startup for local/development environments
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
