using System;
using MediatR;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Customers.Commands.UpdateCustomer
{
    public record UpdateCustomerCommand(Guid Id, string Name, string Email, string Rnc) : IRequest<Result>;
}
