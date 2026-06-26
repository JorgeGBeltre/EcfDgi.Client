using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using EcfDgii.Client.Application.Ecf.Commands.SendRfce;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.UnitTests.Ecf
{
    public class SendRfceCommandHandlerTests
    {
        private readonly Mock<IEcfClient> _ecfClientMock;
        private readonly Mock<IEcfXmlSerializer> _serializerMock;
        private readonly Mock<IEcfDocumentRepository> _documentRepositoryMock;
        private readonly Mock<IUnitOfWork> _unitOfWorkMock;
        private readonly SendRfceCommandHandler _handler;

        public SendRfceCommandHandlerTests()
        {
            _ecfClientMock = new Mock<IEcfClient>();
            _serializerMock = new Mock<IEcfXmlSerializer>();
            _documentRepositoryMock = new Mock<IEcfDocumentRepository>();
            _unitOfWorkMock = new Mock<IUnitOfWork>();
            _handler = new SendRfceCommandHandler(
                _ecfClientMock.Object,
                _serializerMock.Object,
                _documentRepositoryMock.Object,
                _unitOfWorkMock.Object);
        }

        [Fact]
        public async Task Handle_ValidRequest_ShouldSendRfceAndSaveToDb()
        {
            // Arrange
            var rfce = new Rfce
            {
                Encabezado = new RfceEncabezado
                {
                    Emisor = new RfceEmisor { RncEmisor = "101672919" },
                    IdDoc = new RfceIdDoc { ENcf = "E320000000001" },
                    Totales = new RfceTotales { MontoTotal = 1000.0m }
                }
            };
            var command = new SendRfceCommand(rfce);

            var sdkResponse = new RfceRecepcionResponse { Estado = "Aceptado", Codigo = 0 };
            _ecfClientMock.Setup(client => client.SendRfceAsync(rfce, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sdkResponse);

            _serializerMock.Setup(s => s.Serialize(rfce))
                .Returns("<RFCE></RFCE>");

            _documentRepositoryMock.Setup(repo => repo.AddAsync(It.IsAny<EcfDocument>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _unitOfWorkMock.Setup(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            // Act
            var result = await _handler.Handle(command, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Equal("Aceptado", result.Value?.Estado);
            _ecfClientMock.Verify(client => client.SendRfceAsync(rfce, It.IsAny<CancellationToken>()), Times.Once);
            _documentRepositoryMock.Verify(repo => repo.AddAsync(It.Is<EcfDocument>(doc => doc.ENcf == "E320000000001" && doc.State == "Aceptado"), It.IsAny<CancellationToken>()), Times.Once);
            _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
