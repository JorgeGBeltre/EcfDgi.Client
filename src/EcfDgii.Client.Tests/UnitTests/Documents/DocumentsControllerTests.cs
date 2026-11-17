using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Api.Controllers;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Shared.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace EcfDgii.Client.UnitTests.Documents
{
    public class ThrowOnceOnInsertDbContext : ApplicationDbContext
    {
        private readonly string _dbName;
        private readonly DbUpdateException? _exceptionToThrow;
        private readonly EcfDocument? _winnerToPlant;
        private bool _thrown;

        public ThrowOnceOnInsertDbContext(
            DbContextOptions<ApplicationDbContext> options,
            string dbName,
            DbUpdateException? exceptionToThrow,
            EcfDocument? winnerToPlant)
            : base(options)
        {
            _dbName = dbName;
            _exceptionToThrow = exceptionToThrow;
            _winnerToPlant = winnerToPlant;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var isDocumentInsert = ChangeTracker.Entries<EcfDocument>()
                .Any(e => e.State == EntityState.Added);

            if (!_thrown && isDocumentInsert && _exceptionToThrow != null)
            {
                _thrown = true;

                if (_winnerToPlant != null)
                {
                    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                        .UseInMemoryDatabase(_dbName)
                        .Options;
                    using var winnerContext = new ApplicationDbContext(options);
                    winnerContext.EcfDocuments.Add(_winnerToPlant);
                    await winnerContext.SaveChangesAsync(cancellationToken);
                }

                throw _exceptionToThrow;
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    public class DocumentsControllerTests
    {
        private const string FakeSignedXml =
            "<ECF xmlns:ds='http://www.w3.org/2000/09/xmldsig#'>" +
            "<ds:Signature><ds:SignatureValue>ZmFrZS1zaWduYXR1cmU=</ds:SignatureValue></ds:Signature>" +
            "</ECF>";

        private static ApplicationDbContext NewDb()
        {
            return NewDbWithName(Guid.NewGuid().ToString());
        }

        private static ApplicationDbContext NewDbWithName(string name)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name)
                .Options;
            return new ApplicationDbContext(options);
        }

        private static CanonicalDocumentDto MakeDto(string txnId, string editSequence)
        {
            return new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = txnId, EditSequence = editSequence },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 }
            };
        }

        private static DocumentsController MakeController(
            ApplicationDbContext db,
            Mock<IEcfSequenceManager> sequenceManagerMock,
            Mock<IEcfClient> ecfClientMock,
            Mock<IEcfXmlSigner> signerMock,
            Mock<IClock>? clockMock = null)
        {
            if (clockMock == null)
            {
                clockMock = new Mock<IClock>();
                clockMock.Setup(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow.AddDays(1));
            }

            var emisorOptions = Options.Create(new EcfEmisorOptions { Rnc = "101000001", RazonSocial = "Empresa Demo SRL" });
            var ecfClientOptions = Options.Create(new EcfClientOptions { ValidateSchemasLocal = false });

            var controller = new DocumentsController(
                db, sequenceManagerMock.Object, ecfClientMock.Object, signerMock.Object,
                NullLogger<DocumentsController>.Instance, clockMock.Object, emisorOptions,
                new EcfSchemaValidator(), ecfClientOptions)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return controller;
        }

        private static DbUpdateException MakeUniqueViolation(string sqlState, string? constraintName)
        {
            var pgEx = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState, constraintName: constraintName);
            return new DbUpdateException("conflict", pgEx);
        }

        [Fact]
        public async Task NewDocument_UsesConfiguredEmisorRnc_RegardlessOfWhatDtoSends()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000601");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-601" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var dto = MakeDto("TXN-WRONGRNC", "1");
            dto.Header.RncEmisor = "999999999";

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(dto);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("101000001", stored.RncEmisor);
            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), "101000001"), Times.Once);
        }

        [Fact]
        public async Task NewDocument_UsesConfiguredEmisorRazonSocial_RegardlessOfWhatDtoSends()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000602");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-602" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => xml.Replace(
                    "</ECF>",
                    "<ds:Signature xmlns:ds='http://www.w3.org/2000/09/xmldsig#'>" +
                    "<ds:SignatureValue>ZmFrZS1zaWduYXR1cmU=</ds:SignatureValue></ds:Signature></ECF>"));

            var dto = MakeDto("TXN-WRONGRAZON", "1");
            dto.Header.RazonSocialEmisor = "Empresa Impostora SRL";

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(dto);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<RazonSocialEmisor>Empresa Demo SRL</RazonSocialEmisor>", stored.SignedXmlContent);
            Assert.DoesNotContain("Empresa Impostora", stored.SignedXmlContent);
        }

        [Fact]
        public async Task NewDocument_Tipo34_MissingCorrectsENcf_ReturnsBadRequest_BeforeAllocatingASequence()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-NC-1", "1");
            dto.TipoComprobante = "E34";
            dto.References = new CanonicalReferencesDto();

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
            sequenceManagerMock.Verify(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task NewDocument_Tipo34_MissingCodigoModificacion_ReturnsBadRequest()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-NC-2", "1");
            dto.TipoComprobante = "E34";
            dto.References = new CanonicalReferencesDto { CorrectsENcf = "E310000000001" };

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task NewDocument_Tipo34_WithValidReferences_EmitsInformacionReferenciaBlock()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E34", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E340000000001");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-NC" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var dto = MakeDto("TXN-NC-3", "1");
            dto.TipoComprobante = "E34";
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "E310000000001",
                CodigoModificacion = 3,
                RazonModificacion = "error en precio",
            };

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<TipoeCF>34</TipoeCF>", stored.XmlContent);
            Assert.Contains("<NCFModificado>E310000000001</NCFModificado>", stored.XmlContent);
            Assert.Contains("<CodigoModificacion>3</CodigoModificacion>", stored.XmlContent);
            Assert.Contains("<RazonModificacion>error en precio</RazonModificacion>", stored.XmlContent);
        }

        [Fact]
        public async Task NewDocument_Tipo34_FechaNcfModificado_MoreThan30Days_EmitsIndicadorNotaCredito1()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E34", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E340000000049");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-NC-49" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var dto = MakeDto("TXN-NC-49", "1");
            dto.TipoComprobante = "E34";
            dto.Header.FechaEmision = "2026-10-21";
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "E310000000008",
                CodigoModificacion = 3,
                FechaNcfModificado = "2026-09-16",
                RazonModificacion = "Nota de Crédito"
            };

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<IndicadorNotaCredito>1</IndicadorNotaCredito>", stored.XmlContent);
        }

        [Fact]
        public async Task NewDocument_Tipo34_FechaNcfModificado_Within30Days_EmitsIndicadorNotaCredito0()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E34", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E340000000050");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-NC-50" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var dto = MakeDto("TXN-NC-50", "1");
            dto.TipoComprobante = "E34";
            dto.Header.FechaEmision = "2026-10-21";
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "E310000000008",
                CodigoModificacion = 3,
                FechaNcfModificado = "2026-10-05",
                RazonModificacion = "Nota de Crédito"
            };

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<IndicadorNotaCredito>0</IndicadorNotaCredito>", stored.XmlContent);
        }

        [Fact]
        public async Task NewDocument_Tipo41_MissingRncComprador_ReturnsBadRequest()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-COMPRA-1", "1");
            dto.TipoComprobante = "E41";
            dto.Header.RncComprador = "";
            dto.Retention = new CanonicalRetentionDto { MontoItbisRetenido = 18m };

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task NewDocument_Tipo41_MissingRetention_ReturnsBadRequest()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-COMPRA-2", "1");
            dto.TipoComprobante = "E41";
            dto.Retention = null;

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task NewDocument_Tipo41_EmisorStaysConfiguredIdentity_ComprarorIsTheVendor_RetencionEmitted_NoTipoIngresos()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E41", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E410000000001");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-COMPRA" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var dto = MakeDto("TXN-COMPRA-3", "1");
            dto.TipoComprobante = "E41";
            dto.Header.RncComprador = "130000005";
            dto.Header.RazonSocialComprador = "Proveedor Informal SRL";
            dto.Retention = new CanonicalRetentionDto
            {
                IndicadorAgenteRetencionoPercepcion = 1,
                MontoItbisRetenido = 18m,
                MontoIsrRetenido = null,
            };

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("101000001", stored.RncEmisor);
            Assert.Contains("<RNCEmisor>101000001</RNCEmisor>", stored.XmlContent);
            Assert.Contains("<RNCComprador>130000005</RNCComprador>", stored.XmlContent);
            Assert.DoesNotContain("<TipoIngresos>", stored.XmlContent);
            Assert.Contains("<IndicadorAgenteRetencionoPercepcion>1</IndicadorAgenteRetencionoPercepcion>", stored.XmlContent);
            Assert.Contains("<MontoITBISRetenido>18.00</MontoITBISRetenido>", stored.XmlContent);
            Assert.DoesNotContain("<MontoISRRetenido>", stored.XmlContent);
        }

        [Fact]
        public async Task NewDocument_Tipo31_StillEmitsTipoIngresos_AndNoRetencionBlock()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000701");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-701" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-701", "1"));

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<TipoIngresos>01</TipoIngresos>", stored.XmlContent);
            Assert.DoesNotContain("<Retencion>", stored.XmlContent);
            Assert.DoesNotContain("<InformacionReferencia>", stored.XmlContent);
        }

        [Fact]
        public async Task Retry_AfterSigningFailure_ReusesAllocatedEncf_InsteadOfStayingStuck()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000001");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("certificate not found"));

            var dto = MakeDto("TXN-1", "10");
            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var firstResult = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(firstResult);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SigningFailed", stored.State);
            Assert.Equal("E310000000001", stored.ENcf);

            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-1" });

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var secondResult = await controller2.SubmitCanonicalDocument(MakeDto("TXN-1", "10"));

            var accepted = Assert.IsType<AcceptedResult>(secondResult);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed",afterRetry.State);
            Assert.Equal("E310000000001", afterRetry.ENcf);

            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_QueriesDgiiFirst_AndResendsOnlyWhenDgiiNeverReceivedIt()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000002");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-2", "1"));

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", stored.State);
            Assert.Equal("E310000000002", stored.ENcf);
            Assert.Null(stored.TrackId);

            ecfClientMock.Setup(c => c.ConsultarEstadoAsync("101000001", "E310000000002", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Codigo = "0", Estado = "No encontrado" });
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-2" });

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller2.SubmitCanonicalDocument(MakeDto("TXN-2", "1"));

            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed",afterRetry.State);
            Assert.Equal("TRACK-2", afterRetry.TrackId);
            Assert.Equal("E310000000002", afterRetry.ENcf);

            ecfClientMock.Verify(
                c => c.ConsultarEstadoAsync("101000001", "E310000000002", null, null, It.IsAny<CancellationToken>()),
                Times.Once);
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_TooSoonAfterFailure_SkipsReconciliation_StaysUncertain()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000010");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-SOON", "1"));

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", stored.State);

            var tooSoonClockMock = new Mock<IClock>();
            tooSoonClockMock.Setup(c => c.UtcNow)
                .Returns(() => new DateTimeOffset(stored.UpdatedAt!.Value, TimeSpan.Zero).AddSeconds(5));

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock, tooSoonClockMock);
            var result = await controller2.SubmitCanonicalDocument(MakeDto("TXN-SOON", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", afterRetry.State);

            ecfClientMock.Verify(
                c => c.ConsultarEstadoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_ReconciliationQueryThrows_StaysUncertain_DoesNotResend()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000011");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-DGIIDOWN", "1"));
            Assert.Equal("Uncertain", (await db.EcfDocuments.SingleAsync()).State);

            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("DGII unreachable"));

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller2.SubmitCanonicalDocument(MakeDto("TXN-DGIIDOWN", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", afterRetry.State);
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_DoesNotResend_WhenDgiiAlreadyHasIt()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000009");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-9", "1"));
            Assert.Equal("Uncertain", (await db.EcfDocuments.SingleAsync()).State);

            ecfClientMock.Setup(c => c.ConsultarEstadoAsync("101000001", "E310000000009", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Codigo = "1", Estado = "Aceptado" });

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller2.SubmitCanonicalDocument(MakeDto("TXN-9", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed",afterRetry.State);
            Assert.Equal("E310000000009", afterRetry.ENcf);

            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SequenceAllocated_Retry_ReusesEncf_InsteadOfSilentlyReturningUnsentDocument()
        {
            var db = NewDb();
            var crashedDoc = new EcfDocument
            {
                TenantId = "default-tenant",
                SourceTxnId = "TXN-5",
                EditSequence = "1",
                ENcf = "E310000000005",
                RncEmisor = "101000001",
                XmlContent = "<ECF><IdDoc><eNCF>E310000000005</eNCF></IdDoc></ECF>",
                State = "SequenceAllocated"
            };
            db.EcfDocuments.Add(crashedDoc);
            await db.SaveChangesAsync();

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-5" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(MakeDto("TXN-5", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed",afterRetry.State);
            Assert.Equal("TRACK-5", afterRetry.TrackId);
            Assert.Equal("E310000000005", afterRetry.ENcf);

            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SigningFailed_EditSequenceChanged_RetriesWithNewContent_ReusesEncf_NotConflict()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000006");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("certificate not found"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-6", "1"));
            Assert.Equal("SigningFailed", (await db.EcfDocuments.SingleAsync()).State);

            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-6" });

            var editedDto = MakeDto("TXN-6", "2");
            editedDto.Totals = new CanonicalTotalsDto { MontoSubtotal = 200, MontoItbis = 36, MontoTotal = 236 };

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller2.SubmitCanonicalDocument(editedDto);

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed",afterRetry.State);
            Assert.Equal("E310000000006", afterRetry.ENcf);
            Assert.Equal("2", afterRetry.EditSequence);
            Assert.Equal(236, afterRetry.TotalAmount);

            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SameTxnIdDifferentEditSequence_ReturnsConflict_InsteadOfStaleCachedResult()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000003");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-3" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var first = await controller.SubmitCanonicalDocument(MakeDto("TXN-3", "1"));
            Assert.IsType<AcceptedResult>(first);

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var second = await controller2.SubmitCanonicalDocument(MakeDto("TXN-3", "2"));

            Assert.IsType<ConflictObjectResult>(second);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed",stored.State);
        }

        [Fact]
        public async Task SameTxnIdSameEditSequence_ReturnsCachedResult_WithoutReprocessing()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000004");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-4" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-4", "5"));

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var second = await controller2.SubmitCanonicalDocument(MakeDto("TXN-4", "5"));

            Assert.IsType<AcceptedResult>(second);
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ConcurrentInsert_TenantTxnUniqueViolation_RecoversWithWinnersDocument()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;

            var winner = new EcfDocument
            {
                TenantId = "default-tenant",
                SourceTxnId = "TXN-RACE",
                EditSequence = "1",
                ENcf = "E310000000201",
                RncEmisor = "101000001",
                TrackId = "TRACK-WINNER",
                XmlContent = "<ECF/>",
                State = "Signed"
            };

            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23505", "uq_ecf_documents_tenant_source_txn"),
                winner);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000202");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(MakeDto("TXN-RACE", "1"));

            var accepted = Assert.IsType<AcceptedResult>(result);
            var value = Assert.IsAssignableFrom<object>(accepted.Value);
            var eNcfProperty = value.GetType().GetProperty("eNcf");
            Assert.Equal("E310000000201", eNcfProperty!.GetValue(value));

            Assert.Equal(1, await db.EcfDocuments.CountAsync());
            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ConcurrentInsert_UnrelatedUniqueViolation_PropagatesInsteadOfBeingTreatedAsTxnRace()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23505", "uq_ecf_documents_rnc_emisor_encf"),
                winnerToPlant: null);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000301");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => controller.SubmitCanonicalDocument(MakeDto("TXN-OTHER", "1")));
        }

        [Fact]
        public async Task ConcurrentInsert_NonUniqueViolation_Propagates()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23503", null),
                winnerToPlant: null);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000401");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => controller.SubmitCanonicalDocument(MakeDto("TXN-FK", "1")));
        }

        [Fact]
        public async Task ConcurrentInsert_MatchingViolationButNoWinnerFound_ThrowsClearError_NotNullReference()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23505", "uq_ecf_documents_tenant_source_txn"),
                winnerToPlant: null);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000501");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.SubmitCanonicalDocument(MakeDto("TXN-GHOST", "1")));
            Assert.Contains("TXN-GHOST", ex.Message);
        }

        [Fact]
        public async Task MultiTenant_DifferentTenantsAndEnvironments_AllocateIndependentSequenceScopes()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var requestedScopes = new List<string>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((scope, tipo, ct) => requestedScopes.Add(scope))
                .ReturnsAsync("E310000000001");

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-MT" });

            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            var dtoTenantA = MakeDto("TXN-A", "1");
            dtoTenantA.TenantId = "tenant-a";
            dtoTenantA.Environment = "TestEcf";
            dtoTenantA.Header.RncEmisor = "130000002";
            dtoTenantA.Header.RazonSocialEmisor = "Comercial Ejemplo SRL";

            await controller.SubmitCanonicalDocument(dtoTenantA);

            var dtoTenantB = MakeDto("TXN-B", "1");
            dtoTenantB.TenantId = "tenant-b";
            dtoTenantB.Environment = "CertEcf";
            dtoTenantB.Header.RncEmisor = "101000001";
            dtoTenantB.Header.RazonSocialEmisor = "Empresa Demo SRL";

            await controller.SubmitCanonicalDocument(dtoTenantB);

            var dtoTenantAProd = MakeDto("TXN-A-PROD", "1");
            dtoTenantAProd.TenantId = "tenant-a";
            dtoTenantAProd.Environment = "Produccion";
            dtoTenantAProd.Header.RncEmisor = "130000002";
            dtoTenantAProd.Header.RazonSocialEmisor = "Comercial Ejemplo SRL";

            await controller.SubmitCanonicalDocument(dtoTenantAProd);

            Assert.Equal(3, requestedScopes.Count);
            Assert.Equal("tenant-a:PreCertificacion", requestedScopes[0]);
            Assert.Equal("tenant-b:Certificacion", requestedScopes[1]);
            Assert.Equal("tenant-a:Produccion", requestedScopes[2]);

            var docs = await db.EcfDocuments.OrderBy(d => d.CreatedAt).ToListAsync();
            Assert.Equal("tenant-a", docs[0].TenantId);
            Assert.Equal("130000002", docs[0].RncEmisor);
            Assert.Equal("tenant-b", docs[1].TenantId);
            Assert.Equal("101000001", docs[1].RncEmisor);
            Assert.Equal("tenant-a", docs[2].TenantId);
            Assert.Equal("130000002", docs[2].RncEmisor);
        }

        [Fact]
        public async Task DifferentEnvironments_CanUseSameEncfSequence_WithoutCollision()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000001");

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-1" });

            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            var dtoCert = MakeDto("TXN-CERT-1", "1");
            dtoCert.TenantId = "sede-principal";
            dtoCert.Environment = "Certificacion";
            var resultCert = await controller.SubmitCanonicalDocument(dtoCert);
            Assert.IsType<AcceptedResult>(resultCert);

            var dtoProd = MakeDto("TXN-PROD-1", "1");
            dtoProd.TenantId = "sede-principal";
            dtoProd.Environment = "Produccion";
            var resultProd = await controller.SubmitCanonicalDocument(dtoProd);
            Assert.IsType<AcceptedResult>(resultProd);

            var docs = await db.EcfDocuments.OrderBy(d => d.CreatedAt).ToListAsync();
            Assert.Equal(2, docs.Count);
            Assert.Equal("Certificacion", docs[0].Ambiente);
            Assert.Equal("E310000000001", docs[0].ENcf);
            Assert.Equal("Produccion", docs[1].Ambiente);
            Assert.Equal("E310000000001", docs[1].ENcf);
        }

        [Fact]
        public void EcfDocument_UniqueIndexes_IncludeAmbiente_ToPreventCrossEnvironmentCollisions()
        {
            var db = NewDb();
            var entityType = db.Model.FindEntityType(typeof(EcfDocument));
            Assert.NotNull(entityType);

            var rncEncfIndex = entityType.GetIndexes()
                .FirstOrDefault(i => i.GetDatabaseName() == "uq_ecf_documents_rnc_emisor_encf");
            Assert.NotNull(rncEncfIndex);
            Assert.True(rncEncfIndex.IsUnique);
            var rncEncfProps = rncEncfIndex.Properties.Select(p => p.Name).ToList();
            Assert.Contains("RncEmisor", rncEncfProps);
            Assert.Contains("ENcf", rncEncfProps);
            Assert.Contains("Ambiente", rncEncfProps);

            var tenantTxnIndex = entityType.GetIndexes()
                .FirstOrDefault(i => i.GetDatabaseName() == "uq_ecf_documents_tenant_source_txn");
            Assert.NotNull(tenantTxnIndex);
            Assert.True(tenantTxnIndex.IsUnique);
            var tenantTxnProps = tenantTxnIndex.Properties.Select(p => p.Name).ToList();
            Assert.Contains("TenantId", tenantTxnProps);
            Assert.Contains("SourceTxnId", tenantTxnProps);
            Assert.Contains("Ambiente", tenantTxnProps);
        }

        [Fact]
        public async Task GetBySourceTxnId_And_ReconcileBySourceTxnId_IsolateByEnvironment_AndReuseSignedRfceVerbatim()
        {
            var db = NewDb();
            const string exactProdRfce = "<RFCE><Encabezado><Emisor><FechaEmision>04-11-2026</FechaEmision></Emisor><Comprador><RazonSocialComprador>COMERCIO DEMO</RazonSocialComprador></Comprador><CodigoSeguridadeCF>DmY123</CodigoSeguridadeCF></Encabezado></RFCE>";

            db.EcfDocuments.Add(new EcfDocument
            {
                TenantId = "sede-principal-101000001",
                Ambiente = "PreCertificacion",
                RncEmisor = "101000001",
                ENcf = "E320000000190",
                SourceTxnId = "TEST-TXN-OLD",
                EditSequence = "1",
                State = "AcceptedByDgii",
                TrackId = "mBhTUD",
                SecurityCode = "mBhTUD",
                TotalAmount = 3592.43m,
                ItbisAmount = 548.00m,
                CreatedAt = DateTime.UtcNow.AddDays(-40),
                XmlContent = "<ECF/>",
                SignedXmlContent = FakeSignedXml
            });

            db.EcfDocuments.Add(new EcfDocument
            {
                TenantId = "sede-principal-101000001",
                Ambiente = "Produccion",
                RncEmisor = "101000001",
                ENcf = "E320000000190",
                SourceTxnId = "TXN-DEMO-0190",
                EditSequence = "1",
                State = "Uncertain",
                TrackId = null,
                SecurityCode = "DmY123",
                TotalAmount = 1343.74m,
                ItbisAmount = 204.98m,
                CreatedAt = DateTime.UtcNow.AddMinutes(-15),
                XmlContent = "<ECF><Encabezado><Emisor><FechaEmision>04-11-2026</FechaEmision></Emisor><Comprador><RazonSocialComprador>COMERCIO DEMO</RazonSocialComprador></Comprador></Encabezado></ECF>",
                SignedXmlContent = FakeSignedXml,
                SignedRfceContent = exactProdRfce
            });
            await db.SaveChangesAsync();

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync("101000001", "E320000000190", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "No encontrado" });
            ecfClientMock.Setup(c => c.SendRfceAsync(exactProdRfce, "101000001E320000000190.xml", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RfceRecepcionResponse { Codigo = 1, Estado = "Aceptado" });

            var signerMock = new Mock<IEcfXmlSigner>();
            var clockMock = new Mock<IClock>();
            clockMock.Setup(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow.AddHours(1));

            var emisorOptions = Options.Create(new EcfEmisorOptions { Rnc = "101000001", RazonSocial = "Empresa Demo SRL" });
            var ecfClientOptions = Options.Create(new EcfClientOptions { Environment = EcfEnvironment.Prod, ValidateSchemasLocal = false });

            var controller = new DocumentsController(
                db, sequenceManagerMock.Object, ecfClientMock.Object, signerMock.Object,
                NullLogger<DocumentsController>.Instance, clockMock.Object, emisorOptions,
                new EcfSchemaValidator(), ecfClientOptions)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers["X-Tenant-Id"] = "sede-principal-101000001";

            var lookupRes = Assert.IsType<OkObjectResult>(await controller.GetBySourceTxnId("E320000000190", "Produccion"));
            var lookupJson = System.Text.Json.JsonSerializer.Serialize(lookupRes.Value);
            Assert.Contains("\"state\":\"Uncertain\"", lookupJson);
            Assert.Contains("\"securityCode\":\"DmY123\"", lookupJson);
            Assert.Contains("\"environment\":\"Produccion\"", lookupJson);

            var reconcileRes = Assert.IsType<OkObjectResult>(await controller.ReconcileBySourceTxnId("E320000000190", "Produccion"));
            var reconcileJson = System.Text.Json.JsonSerializer.Serialize(reconcileRes.Value);
            Assert.Contains("\"state\":\"AcceptedByDgii\"", reconcileJson);
            Assert.Contains("\"securityCode\":\"DmY123\"", reconcileJson);

            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            ecfClientMock.Verify(c => c.SendRfceAsync(exactProdRfce, "101000001E320000000190.xml", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CreditNote_WhenTotalExceedsModifiedEcfDocument_ReturnsBadRequestRule615WithoutAllocatingSequence()
        {
            var db = NewDb();
            db.EcfDocuments.Add(new EcfDocument
            {
                TenantId = "sede-principal",
                Ambiente = "Produccion",
                RncEmisor = "130000002",
                ENcf = "E310000000010",
                SourceTxnId = "ORIG-100000010",
                EditSequence = "100000010",
                State = "AcceptedByDgii",
                TrackId = "TRACK-ORIG-010",
                SecurityCode = "ABC123",
                TotalAmount = 59000.00m,
                ItbisAmount = 9000.00m,
                CreatedAt = DateTime.UtcNow.AddDays(-20),
                XmlContent = "<ECF/>",
                SignedXmlContent = FakeSignedXml
            });
            await db.SaveChangesAsync();

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var dto = MakeDto("TXN-CRN-0001", "400000001");
            dto.TenantId = "sede-principal";
            dto.Environment = "Produccion";
            dto.TipoComprobante = "E34";
            dto.Header.RncEmisor = "130000002";
            dto.Header.RazonSocialEmisor = "COMERCIAL EJEMPLO SRL";
            dto.Header.RncComprador = "130000001";
            dto.Header.RazonSocialComprador = "CLIENTE CORPORATIVO SRL";
            dto.Totals = new CanonicalTotalsDto
            {
                MontoSubtotal = 5000000.00m,
                MontoGravadoTotal = 5000000.00m,
                MontoItbis = 900000.00m,
                MontoTotal = 5900000.00m
            };
            dto.Lines = new List<CanonicalLineDto>
            {
                new()
                {
                    LineNumber = 1,
                    ItemName = "Producto Estándar Tipo A",
                    Quantity = 100m,
                    UnitPrice = 50000.00m,
                    Amount = 5000000.00m,
                    IndicadorFacturacion = "1"
                }
            };
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "E310000000010",
                CodigoModificacion = 3,
                FechaNcfModificado = "2026-10-16",
                RazonModificacion = "Devolución de mercancía"
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            var json = System.Text.Json.JsonSerializer.Serialize(badReq.Value);
            Assert.Contains("[615]", json);

            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SubmitCanonicalDocument_WhenConsultarResultadoReturnsAceptadoImmediately_TransitionsDirectlyToAcceptedByDgii()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000795");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-IMM-795" });
            ecfClientMock.Setup(c => c.ConsultarResultadoAsync("TRACK-IMM-795", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaResultadoResponse { Estado = "Aceptado" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(MakeDto("TXN-IMM-795", "1"));

            var accepted = Assert.IsType<AcceptedResult>(result);
            var json = System.Text.Json.JsonSerializer.Serialize(accepted.Value);
            Assert.Contains("AcceptedByDgii", json);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("AcceptedByDgii", stored.State);
            Assert.Equal("TRACK-IMM-795", stored.TrackId);
            Assert.Equal(1, stored.StatusCheckAttempts);
            Assert.NotNull(stored.LastStatusCheckAt);
        }

        [Fact]
        public async Task GetBySourceTxnId_WhenDocumentIsSignedAndDgiiConfirmedAceptado_DynamicallyReconcilesAndPersistsAcceptedByDgii()
        {
            var db = NewDb();
            db.EcfDocuments.Add(new EcfDocument
            {
                TenantId = "default-tenant",
                SourceTxnId = "TXN-LIVE-795",
                EditSequence = "1",
                ENcf = "E310000000796",
                RncEmisor = "101000001",
                RncComprador = "130000000",
                TrackId = "TRACK-LIVE-796",
                SecurityCode = "AbC123",
                Ambiente = "PreCertificacion",
                State = "Signed",
                SentToDgiiAt = DateTime.UtcNow.AddSeconds(-6),
                XmlContent = "<ECF/>",
                SignedXmlContent = FakeSignedXml
            });
            await db.SaveChangesAsync();

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarResultadoAsync("TRACK-LIVE-796", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaResultadoResponse { Estado = "Aceptado" });
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.GetBySourceTxnId("TXN-LIVE-795");

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            Assert.Contains("AcceptedByDgii", json);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("AcceptedByDgii", stored.State);
            Assert.Equal(1, stored.StatusCheckAttempts);
        }

        [Fact]
        public async Task CreditNote_ModifyingE31_WithDuplicateIdentificadorExtranjero_EmitsRncCompradorAndOmitsIdentificadorExtranjero()
        {
            var db = NewDb();
            db.EcfDocuments.Add(new EcfDocument
            {
                TenantId = "default-tenant",
                Ambiente = "PreCertificacion",
                RncEmisor = "101000001",
                RncComprador = "00100000018",
                ENcf = "E310000000050",
                SourceTxnId = "ORIG-100000050",
                EditSequence = "100000050",
                State = "AcceptedByDgii",
                TrackId = "TRACK-ORIG-050",
                SecurityCode = "ABC050",
                TotalAmount = 2000000.00m,
                ItbisAmount = 305084.75m,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                XmlContent = "<ECF><Encabezado><Comprador><RNCComprador>00100000018</RNCComprador><RazonSocialComprador>CLIENTE DEMO</RazonSocialComprador></Comprador></Encabezado></ECF>",
                SignedXmlContent = "<ECF><Encabezado><Comprador><RNCComprador>00100000018</RNCComprador><RazonSocialComprador>CLIENTE DEMO</RazonSocialComprador></Comprador></Encabezado></ECF>"
            });
            await db.SaveChangesAsync();

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock
                .Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E34", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E340000000050");

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock
                .Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-CRN-050" });
            ecfClientMock
                .Setup(c => c.ConsultarResultadoAsync("TRACK-CRN-050", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaResultadoResponse { Estado = "Aceptado" });

            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock
                .Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var dto = MakeDto("TXN-CRN-1381-A", "400000050");
            dto.TipoComprobante = "E34";
            dto.Header.RncComprador = "00100000018";
            dto.Header.IdentificadorExtranjero = "00100000018";
            dto.Header.RazonSocialComprador = "CLIENTE DEMO";
            dto.Totals = new CanonicalTotalsDto
            {
                MontoSubtotal = 1000000.00m,
                MontoGravadoTotal = 1000000.00m,
                MontoItbis = 180000.00m,
                MontoTotal = 1180000.00m
            };
            dto.Lines = new List<CanonicalLineDto>
            {
                new()
                {
                    LineNumber = 1,
                    ItemName = "Descuento por ajuste de precios",
                    Quantity = 1m,
                    UnitPrice = 1000000.00m,
                    Amount = 1000000.00m,
                    IndicadorFacturacion = "1"
                }
            };
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "E310000000050",
                CodigoModificacion = 3,
                FechaNcfModificado = "2026-10-28",
                RazonModificacion = "B0100000050"
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync(d => d.ENcf == "E340000000050");
            Assert.Contains("<RNCComprador>00100000018</RNCComprador>", stored.XmlContent);
            Assert.DoesNotContain("<IdentificadorExtranjero>", stored.XmlContent);
            Assert.Equal("AcceptedByDgii", stored.State);
        }

        [Fact]
        public async Task CreditNote_ModifyingB01_WithoutRncComprador_ReturnsBadRequestRule1381WithoutAllocatingSequence()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var dto = MakeDto("TXN-CRN-1381-B", "400000051");
            dto.TenantId = "sede-principal";
            dto.Environment = "Produccion";
            dto.TipoComprobante = "E34";
            dto.Header.RncEmisor = "130000002";
            dto.Header.RazonSocialEmisor = "COMERCIAL EJEMPLO SRL";
            dto.Header.RncComprador = "";
            dto.Header.RazonSocialComprador = "CLIENTE SIN RNC";
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "B0100000099",
                CodigoModificacion = 3,
                FechaNcfModificado = "2026-10-20",
                RazonModificacion = "Ajuste parcial"
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            var json = System.Text.Json.JsonSerializer.Serialize(badReq.Value);
            Assert.Contains("[1381]", json);
            sequenceManagerMock.Verify(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
