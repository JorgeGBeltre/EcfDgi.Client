using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace EcfDgii.Client.Api.Services
{
    public class NullObjectModelValidator : IObjectModelValidator
    {
        public void Validate(ActionContext actionContext, ValidationStateDictionary? validationState, string prefix, object? model)
        {
        }
    }
}
