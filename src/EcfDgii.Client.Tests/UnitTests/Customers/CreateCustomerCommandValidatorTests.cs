using Xunit;
using EcfDgii.Client.Application.Customers.Commands.CreateCustomer;

namespace EcfDgii.Client.UnitTests.Customers
{
    public class CreateCustomerCommandValidatorTests
    {
        private readonly CreateCustomerCommandValidator _validator;

        public CreateCustomerCommandValidatorTests()
        {
            _validator = new CreateCustomerCommandValidator();
        }

        [Fact]
        public void Validate_ValidCommand_ShouldHaveNoErrors()
        {
            var command = new CreateCustomerCommand("Name", "test@test.com", "101672919");
            var result = _validator.Validate(command);
            Assert.True(result.IsValid);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid-email")]
        public void Validate_InvalidEmail_ShouldHaveValidationError(string email)
        {
            var command = new CreateCustomerCommand("Name", email, "101672919");
            var result = _validator.Validate(command);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Email");
        }

        [Theory]
        [InlineData("")]
        [InlineData("12345678")] // 8 digits (invalid)
        [InlineData("1234567890")] // 10 digits (invalid)
        [InlineData("123456789012")] // 12 digits (invalid)
        [InlineData("abcdeflgh")] // characters (invalid)
        public void Validate_InvalidRnc_ShouldHaveValidationError(string rnc)
        {
            var command = new CreateCustomerCommand("Name", "test@test.com", rnc);
            var result = _validator.Validate(command);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Rnc");
        }
    }
}
