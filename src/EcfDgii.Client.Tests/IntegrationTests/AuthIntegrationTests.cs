using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;
using EcfDgii.Client.Application.Auth.Commands.Login;
using EcfDgii.Client.Application.Auth.Commands.Register;
using EcfDgii.Client.Application.Auth.Common;

namespace EcfDgii.Client.IntegrationTests
{
    public class AuthIntegrationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _client;

        public AuthIntegrationTests(CustomWebApplicationFactory factory)
        {
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task Register_ValidCredentials_ReturnsAuthResponse()
        {
            // Arrange
            var command = new RegisterUserCommand("newuser", "newuser@test.com", "Password123!");

            // Act
            var response = await _client.PostAsJsonAsync("api/auth/register", command);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
            Assert.NotNull(result);
            Assert.Equal("newuser", result.Username);
            Assert.False(string.IsNullOrEmpty(result.Token));
        }

        [Fact]
        public async Task Login_ValidUser_ReturnsToken()
        {
            // Arrange (MED-248: Do not entrench static seeded passwords in tests)
            var username = $"user_{Guid.NewGuid():N}"[..12];
            var email = $"{username}@test.com";
            var password = $"P@ss_{Guid.NewGuid():N}123!";
            var registerCmd = new RegisterUserCommand(username, email, password);
            var regResponse = await _client.PostAsJsonAsync("api/auth/register", registerCmd);
            regResponse.EnsureSuccessStatusCode();

            var command = new LoginUserCommand(username, password);

            // Act
            var response = await _client.PostAsJsonAsync("api/auth/login", command);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
            Assert.NotNull(result);
            Assert.Equal(username, result.Username);
            Assert.Equal("User", result.Role);
            Assert.False(string.IsNullOrEmpty(result.Token));
        }
    }
}
