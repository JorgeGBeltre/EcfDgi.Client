using System;
using MediatR;
using EcfDgii.Client.Application.Customers.Common;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Customers.Queries.GetCustomerById
{
    public record GetCustomerByIdQuery(Guid Id) : IRequest<Result<CustomerDto>>;
}
