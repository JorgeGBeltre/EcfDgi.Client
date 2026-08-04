using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;

namespace EcfDgii.Client.IntegrationTests
{
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        public Mock<IEcfClient> EcfClientMock { get; } = new Mock<IEcfClient>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Disable Redis health check in integration tests and force InMemory database
            builder.UseSetting("ConnectionStrings:DefaultConnection", "InMemory");
            builder.UseSetting("ConnectionStrings:Redis", "");

            builder.ConfigureServices(services =>
            {
                // Remove existing IEcfClient registration and inject mock
                var sdkDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IEcfClient));

                if (sdkDescriptor != null)
                {
                    services.Remove(sdkDescriptor);
                }

                services.AddSingleton<IEcfClient>(EcfClientMock.Object);

                // Build service provider and ensure DB created
                var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.EnsureCreated();
            });
        }
    }
}
