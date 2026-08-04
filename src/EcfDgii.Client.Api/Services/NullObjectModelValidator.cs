using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace EcfDgii.Client.Api.Services
{
    public class NullObjectModelValidator : IObjectModelValidator
    {
        public void Validate(ActionContext actionContext, ValidationStateDictionary? validationState, string prefix, object? model)
        {
            // Bypasses ASP.NET Core built-in DataAnnotations model validation visitor.
            // Model validation is handled exclusively by MediatR ValidationBehavior and FluentValidation.
        }
    }
}
