using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Persistence;

namespace EcfDgii.Client.IntegrationTests
{
    /// <summary>
    /// Verifies EcfEmisorOptions' ValidateOnStart actually blocks startup — not just that the
    /// validation predicate is correct in isolation. A DocumentsController-level unit test can't
    /// exercise "the host refuses to start"; only building the real host can.
    /// </summary>
    public class EmisorRncStartupValidationTests
    {
        private static WebApplicationFactory<Program> MakeFactory(string? rnc)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:DefaultConnection", "InMemory");
                builder.UseSetting("ConnectionStrings:Redis", "");
                builder.UseSetting("EcfEmisor:Rnc", rnc);

                builder.ConfigureServices(services =>
                {
                    var sdkDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEcfClient));
                    if (sdkDescriptor != null)
                    {
                        services.Remove(sdkDescriptor);
                    }
                    services.AddSingleton<IEcfClient>(new Mock<IEcfClient>().Object);
                });
            });
        }

        [Fact]
        public void MissingEmisorRnc_PreventsHostFromStarting()
        {
            using var factory = MakeFactory(rnc: null);

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            // ValidateOnStart wraps OptionsValidationException inside the host-start failure;
            // assert on the message rather than a specific exception type so this doesn't become
            // brittle to how ASP.NET Core happens to wrap it in a given version.
            var full = ex!.ToString();
            Assert.Contains("EcfEmisor", full, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MalformedEmisorRnc_PreventsHostFromStarting()
        {
            using var factory = MakeFactory(rnc: "not-a-real-rnc");

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("EcfEmisor", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidEmisorRnc_HostStartsAndOptionsResolveCorrectly()
        {
            using var factory = MakeFactory(rnc: "101889063");

            using var client = factory.CreateClient(); // must not throw

            using var scope = factory.Services.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<EcfEmisorOptions>>();
            Assert.Equal("101889063", options.Value.Rnc);
        }
    }
}
