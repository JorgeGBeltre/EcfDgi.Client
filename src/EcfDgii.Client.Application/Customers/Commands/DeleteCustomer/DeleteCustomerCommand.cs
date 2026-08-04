using System;
using MediatR;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Customers.Commands.DeleteCustomer
{
    public record DeleteCustomerCommand(Guid Id) : IRequest<Result>;
}
