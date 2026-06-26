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
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "InMemory");

            builder.ConfigureServices(services =>
            {
                // Remove existing IEcfClient registration
                var sdkDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IEcfClient));

                if (sdkDescriptor != null)
                {
                    services.Remove(sdkDescriptor);
                }

                // Inject mock SDK Client
                services.AddSingleton<IEcfClient>(EcfClientMock.Object);

                // Build a service provider and seed data
                using var scope = services.BuildServiceProvider().CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.EnsureCreated();
            });
        }
    }
}
