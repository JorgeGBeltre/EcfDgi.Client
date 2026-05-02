using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Entities;
using Xunit;

namespace EcfDgii.Client.Tests
{
    public class EcfClientTests
    {
        private class MockTransport : IEcfTransport
        {
            public bool SendEcfCalled { get; private set; }
            public bool SendRfceCalled { get; private set; }

            public Task<EcfRecepcionResponse> SendEcfAsync(string xmlContent, string fileName, CancellationToken ct = default)
            {
                SendEcfCalled = true;
                return Task.FromResult(new EcfRecepcionResponse { TrackId = "123" });
            }

            public Task<RfceRecepcionResponse> SendRfceAsync(string xmlContent, string fileName, CancellationToken ct = default)
            {
                SendRfceCalled = true;
                return Task.FromResult(new RfceRecepcionResponse { ENcf = "E320000000001", Estado = "Recibido" });
            }

            public Task<ConsultaResultadoResponse> ConsultarResultadoAsync(string trackId, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<ConsultaEstadoResponse> ConsultarEstadoAsync(ConsultaEstadoRequest request, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<List<TrackIdDetalle>> ConsultarTrackIdsAsync(string rncEmisor, string eNcf, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<RfceConsultaResponse> ConsultarRfceAsync(string rncEmisor, string eNcf, string codigoSeguridad, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<AprobacionComercialResponse> SendAprobacionComercialAsync(string xmlContent, string fileName, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<AnulacionResponse> AnularRangosAsync(string xmlContent, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<List<DirectorioContribuyente>> ConsultarDirectorioAsync(CancellationToken ct = default) => throw new NotImplementedException();
            public Task<DirectorioContribuyente> ConsultarDirectorioPorRncAsync(string rnc, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<TimbreResponse> ConsultarTimbreAsync(TimbreEcfRequest request, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<TimbreFcResponse> ConsultarTimbreFcAsync(TimbreFcRequest request, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<List<EstatusServicio>> ConsultarEstatusServiciosAsync(CancellationToken ct = default) => throw new NotImplementedException();
            public Task<List<VentanaMantenimiento>> ConsultarVentanasMantenimientoAsync(CancellationToken ct = default) => throw new NotImplementedException();
            public Task<string> VerificarEstadoAmbienteAsync(AmbienteEnum ambiente, CancellationToken ct = default) => throw new NotImplementedException();
        }

        [Fact]
        public async Task SendRfceAsync_ValidRfce_CallsTransport()
        {
            var mock = new MockTransport();
            var client = new EcfClient(new EcfClientOptions { RncEmisor = "101672919" }, mock);
            
            var rfce = new Rfce
            {
                Encabezado = new RfceEncabezado
                {
                    Emisor = new RfceEmisor { RncEmisor = "101672919", FechaEmision = "02-05-2026" },
                    IdDoc = new RfceIdDoc { TipoeCF = "32", ENcf = "E320000000001" },
                    Totales = new RfceTotales { MontoTotal = 1000.0m }
                }
            };

            await client.SendRfceAsync(rfce);

            Assert.True(mock.SendRfceCalled);
        }
    }
}
