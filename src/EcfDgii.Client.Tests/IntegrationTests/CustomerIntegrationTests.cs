using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;
using EcfDgii.Client.Application.Auth.Commands.Login;
using EcfDgii.Client.Application.Auth.Commands.Register;
using EcfDgii.Client.Application.Auth.Common;
using EcfDgii.Client.Application.Customers.Commands.CreateCustomer;
using EcfDgii.Client.Application.Customers.Common;

namespace EcfDgii.Client.IntegrationTests
{
    public class CustomerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;
        private readonly HttpClient _client;

        public CustomerIntegrationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        private async Task AuthenticateAsync(string? username = null, string? password = null)
        {
            var user = username ?? $"cust_usr_{Guid.NewGuid():N}"[..12];
            var pass = password ?? $"Pass_{Guid.NewGuid():N}123!";
            var email = $"{user}@test.com";
            var registerCmd = new RegisterUserCommand(user, email, pass);
            var regResponse = await _client.PostAsJsonAsync("api/auth/register", registerCmd);
            regResponse.EnsureSuccessStatusCode();

            var loginCmd = new LoginUserCommand(user, pass);
            var response = await _client.PostAsJsonAsync("api/auth/login", loginCmd);
            var result = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
            
            _client.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", result!.Token);
        }

        [Fact]
        public async Task GetCustomers_WithoutAuth_ReturnsUnauthorized()
        {
            // Clear authorization
            _client.DefaultRequestHeaders.Authorization = null;

            // Act
            var response = await _client.GetAsync("api/customers");

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task CreateCustomer_WithAuth_CreatesAndReturns()
        {
            // Arrange (MED-248)
            await AuthenticateAsync();
            var command = new CreateCustomerCommand("Integrator Client", "int@client.com", "101672919");

            // Act
            var response = await _client.PostAsJsonAsync("api/customers", command);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = await response.Content.ReadFromJsonAsync<Guid>();
            Assert.NotEqual(Guid.Empty, id);

            // Verify was added to DB
            var verifyResponse = await _client.GetAsync($"api/customers/{id}");
            Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
            var customer = await verifyResponse.Content.ReadFromJsonAsync<CustomerDto>();
            Assert.Equal("Integrator Client", customer!.Name);
        }
    }
}
