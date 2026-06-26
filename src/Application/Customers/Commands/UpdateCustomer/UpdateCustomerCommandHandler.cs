using System.Threading;
using System.Threading.Tasks;
using MediatR;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Customers.Commands.UpdateCustomer
{
    public class UpdateCustomerCommandHandler : IRequestHandler<UpdateCustomerCommand, Result>
    {
        private readonly ICustomerRepository _customerRepository;
        private readonly IUnitOfWork _unitOfWork;

        public UpdateCustomerCommandHandler(ICustomerRepository customerRepository, IUnitOfWork unitOfWork)
        {
            _customerRepository = customerRepository;
            _unitOfWork = unitOfWork;
        }

        public async Task<Result> Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
        {
            var customer = await _customerRepository.GetByIdAsync(request.Id, cancellationToken);
            if (customer == null)
            {
                return Result.Failure("Customer not found.");
            }

            customer.Name = request.Name;
            customer.Email = request.Email;
            customer.Rnc = request.Rnc;

            _customerRepository.Update(customer);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
    }
}
