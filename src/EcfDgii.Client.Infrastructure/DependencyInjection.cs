using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Infrastructure.Caching;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Persistence.Repositories;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;

namespace EcfDgii.Client.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
        {
            // DB Context
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (connectionString == "InMemory")
            {
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseInMemoryDatabase("InMemoryDbForTesting"));
            }
            else
            {
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseNpgsql(connectionString,
                        b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));
            }

            // Redis Infrastructure Setup
            var redisConnectionString = configuration.GetConnectionString("Redis");
            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                services.AddSingleton<IConnectionMultiplexer>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<RedisCacheService>>();
                    try
                    {
                        var configOptions = ConfigurationOptions.Parse(redisConnectionString);
                        configOptions.AbortOnConnectFail = false;
                        configOptions.ConnectTimeout = 5000;
                        return ConnectionMultiplexer.Connect(configOptions);
                    }
                    catch (System.Exception ex)
                    {
                        logger.LogWarning(ex, "Could not connect to Redis server at '{ConnectionString}'. Cache will operate in bypass/fallback mode.", redisConnectionString);
                        return null!;
                    }
                });

                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = redisConnectionString;
                    options.InstanceName = "EcfDgii:";
                });
            }
            else
            {
                services.AddSingleton<IConnectionMultiplexer>(_ => null!);
            }

            services.AddSingleton<ICacheService, RedisCacheService>();

            // JWT Settings Configuration
            services.Configure<JwtSettings>(configuration.GetSection("JwtSettings"));

            // SDK Options Configuration
            services.Configure<EcfClientOptions>(configuration.GetSection("EcfClientOptions"));
            // XsdDirectoryPath defaults to a path anchored at AppContext.BaseDirectory (where
            // EcfDgii.Client.Api.csproj copies the real DGII XSDs alongside the published app) when
            // config doesn't set one explicitly — this is what actually turns local schema
            // validation ON by default (EcfClient.SendEcfAsync's check, and DocumentsController's own
            // pre-DGII gate, both read this same option). Previously this was always empty/unset
            // anywhere in this repo's config, so the validation code existed but silently never ran.
            services.PostConfigure<EcfClientOptions>(options =>
            {
                if (string.IsNullOrWhiteSpace(options.XsdDirectoryPath))
                {
                    options.XsdDirectoryPath = System.IO.Path.Combine(AppContext.BaseDirectory, "XSD");
                }
            });
            services.AddSingleton<IEcfSchemaValidator, EcfSchemaValidator>();

            // Register Services
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<ICustomerRepository, CustomerRepository>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IEcfDocumentRepository, EcfDocumentRepository>();

            services.AddSingleton<IEcfSequenceManager, EcfSequenceManager>();
            services.AddSingleton<IPasswordHasher, PasswordHasher>();
            services.AddScoped<ITokenService, TokenService>();
            services.AddSingleton<IEcfXmlSerializer, EcfXmlSerializer>();

            services.AddSingleton<IEcfXmlSigner>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<EcfClientOptions>>().Value;
                return new EcfXmlSigner(options.CertificatePath ?? "", options.CertificatePassword ?? "");
            });

            // Register EcfClient wrapped with Redis Caching Decorator
            services.AddSingleton<IEcfClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<EcfClientOptions>>().Value;
                var signer = sp.GetRequiredService<IEcfXmlSigner>();
                var baseClient = new EcfClient(options, signer: signer);
                var cacheService = sp.GetRequiredService<ICacheService>();
                return new CachedEcfClient(baseClient, cacheService);
            });

            return services;
        }
    }
}
