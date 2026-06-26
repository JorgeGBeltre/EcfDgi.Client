using System;
using MediatR;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Customers.Commands.CreateCustomer
{
    public record CreateCustomerCommand(string Name, string Email, string Rnc) : IRequest<Result<Guid>>;
}
