using System.Collections.Generic;
using System.ComponentModel;
using EcfDgii.Client.Domain.Common;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EcfDgii.Client.UnitTests.Common
{
    public class EcfEnvironmentBindingTests
    {
        [Theory]
        [InlineData("Produccion", EcfEnvironment.Prod)]
        [InlineData("produccion", EcfEnvironment.Prod)]
        [InlineData("PRODUCCION", EcfEnvironment.Prod)]
        [InlineData("Prod", EcfEnvironment.Prod)]
        [InlineData("Production", EcfEnvironment.Prod)]
        [InlineData("Ecf", EcfEnvironment.Prod)]
        [InlineData("Certificacion", EcfEnvironment.Cert)]
        [InlineData("certificacion", EcfEnvironment.Cert)]
        [InlineData("Cert", EcfEnvironment.Cert)]
        [InlineData("Certification", EcfEnvironment.Cert)]
        [InlineData("Certecf", EcfEnvironment.Cert)]
        [InlineData("Test", EcfEnvironment.Test)]
        [InlineData("PreCertificacion", EcfEnvironment.Test)]
        [InlineData("TestEcf", EcfEnvironment.Test)]
        public void TypeConverter_ConvertsValuesSuccessfully(string input, EcfEnvironment expected)
        {
            var converter = TypeDescriptor.GetConverter(typeof(EcfEnvironment));
            Assert.True(converter.CanConvertFrom(typeof(string)));

            var result = converter.ConvertFrom(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Produccion", EcfEnvironment.Prod)]
        [InlineData("produccion", EcfEnvironment.Prod)]
        [InlineData("Certificacion", EcfEnvironment.Cert)]
        [InlineData("Test", EcfEnvironment.Test)]
        [InlineData("Production", EcfEnvironment.Prod)]
        public void ConfigurationBinder_BindsEcfClientOptions_WithoutEnumConversionErrors(string envValue, EcfEnvironment expected)
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "EcfClientOptions:Environment", envValue },
                { "EcfClientOptions:RncEmisor", "101889063" }
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var options = new EcfClientOptions();
            configuration.GetSection("EcfClientOptions").Bind(options);

            Assert.Equal(expected, options.Environment);
        }

        [Fact]
        public void ServiceProvider_ResolvesIOptions_WhenEnvironmentIsProduccion()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "Ambiente", "Produccion" },
                { "EcfClientOptions:Environment", "Produccion" },
                { "EcfClientOptions:RncEmisor", "101889063" },
                { "EcfClientOptions:CertificatePath", "dummy.pfx" },
                { "EcfClientOptions:CertificatePassword", "secret" }
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.Configure<EcfClientOptions>(configuration.GetSection("EcfClientOptions"));
            services.PostConfigure<EcfClientOptions>(options =>
            {
                var flatAmbiente = configuration["Ambiente"];
                if (!string.IsNullOrWhiteSpace(flatAmbiente))
                {
                    options.Environment = EcfEnvironmentHelper.ResolveEcfEnvironment(flatAmbiente, options.Environment);
                }
            });

            var sp = services.BuildServiceProvider();
            var resolvedOptions = sp.GetRequiredService<IOptions<EcfClientOptions>>().Value;

            Assert.NotNull(resolvedOptions);
            Assert.Equal(EcfEnvironment.Prod, resolvedOptions.Environment);
        }
    }
}
