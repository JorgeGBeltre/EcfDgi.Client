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

            builder.UseSetting("ConnectionStrings:DefaultConnection", "InMemory");
            builder.UseSetting("ConnectionStrings:Redis", "");

            builder.UseSetting("WorkerSecretKey", "WorkerSecretKey");
            builder.UseSetting("JwtSettings:Secret", "DevOnly_NotForProduction_JwtSigningSecret_MinimumThirtyTwoBytes!");

            builder.ConfigureServices(services =>
            {
                var sdkDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IEcfClient));

                if (sdkDescriptor != null)
                {
                    services.Remove(sdkDescriptor);
                }

                services.AddSingleton<IEcfClient>(EcfClientMock.Object);

                var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.EnsureCreated();
            });
        }
    }
}
