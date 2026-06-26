using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Application.Common.Interfaces;
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

            // JWT Settings Configuration
            services.Configure<JwtSettings>(configuration.GetSection("JwtSettings"));

            // SDK Options Configuration
            services.Configure<EcfClientOptions>(configuration.GetSection("EcfClientOptions"));

            // Register Services
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<ICustomerRepository, CustomerRepository>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IEcfDocumentRepository, EcfDocumentRepository>();

            services.AddSingleton<IPasswordHasher, PasswordHasher>();
            services.AddScoped<ITokenService, TokenService>();
            services.AddSingleton<IEcfXmlSerializer, EcfXmlSerializer>();

            // Register EcfClient from SDK
            services.AddSingleton<IEcfClient, EcfClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<EcfClientOptions>>().Value;
                return new EcfClient(options);
            });

            return services;
        }
    }
}
