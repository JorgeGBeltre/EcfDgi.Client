using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EcfDgii.Client.Application.Customers.Commands.CreateCustomer;
using EcfDgii.Client.Application.Customers.Commands.DeleteCustomer;
using EcfDgii.Client.Application.Customers.Commands.UpdateCustomer;
using EcfDgii.Client.Application.Customers.Common;
using EcfDgii.Client.Application.Customers.Queries.GetAllCustomers;
using EcfDgii.Client.Application.Customers.Queries.GetCustomerById;

namespace EcfDgii.Client.Api.Controllers
{
    [Authorize]
    public class CustomersController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<List<CustomerDto>>> GetAll()
        {
            var result = await Mediator.Send(new GetAllCustomersQuery());
            return Ok(result.Value);
        }

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<CustomerDto>> GetById(Guid id)
        {
            var result = await Mediator.Send(new GetCustomerByIdQuery(id));

            if (result.IsFailure)
            {
                return NotFound(new { error = result.Error });
            }

            return Ok(result.Value);
        }

        [HttpPost]
        public async Task<ActionResult<Guid>> Create(CreateCustomerCommand command)
        {
            var result = await Mediator.Send(command);

            if (result.IsFailure)
            {
                return BadRequest(new { error = result.Error, errors = result.Errors });
            }

            return CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value);
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update(Guid id, UpdateCustomerCommand command)
        {
            if (id != command.Id)
            {
                return BadRequest("Mismatched route ID and request body ID.");
            }

            var result = await Mediator.Send(command);

            if (result.IsFailure)
            {
                return NotFound(new { error = result.Error });
            }

            return NoContent();
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var result = await Mediator.Send(new DeleteCustomerCommand(id));

            if (result.IsFailure)
            {
                return NotFound(new { error = result.Error });
            }

            return NoContent();
        }
    }
}
