using System.Collections.Generic;
using MediatR;
using EcfDgii.Client.Application.Customers.Common;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Customers.Queries.GetAllCustomers
{
    public record GetAllCustomersQuery() : IRequest<Result<List<CustomerDto>>>;
}
