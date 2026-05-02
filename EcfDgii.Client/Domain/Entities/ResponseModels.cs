using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace EcfDgii.Client.Domain.Entities
{
    [XmlRoot("RespuestaRecepcion")]
    public class EcfRecepcionResponse
    {
        [XmlElement("trackId")]
        [JsonPropertyName("trackId")]
        public string TrackId { get; set; }

        [XmlElement("error")]
        [JsonPropertyName("error")]
        public string Error { get; set; }

        [XmlElement("mensaje")]
        [JsonPropertyName("mensaje")]
        public string Mensaje { get; set; }
    }

    [XmlRoot("Respuesta")]
    public class RfceRecepcionResponse
    {
        [XmlElement("codigo")]
        [JsonPropertyName("codigo")]
        public int Codigo { get; set; }

        [XmlElement("estado")]
        [JsonPropertyName("estado")]
        public string Estado { get; set; }

        [XmlArray("mensajes")]
        [XmlArrayItem("MensajeCodigo")] // Check MD for exact tag if nested
        [JsonPropertyName("mensajes")]
        public List<MensajeCodigo> Mensajes { get; set; }

        [XmlElement("encf")]
        [JsonPropertyName("encf")]
        public string ENcf { get; set; }

        [XmlElement("secuenciaUtilizada")]
        [JsonPropertyName("secuenciaUtilizada")]
        public bool SecuenciaUtilizada { get; set; }
    }

    [XmlRoot("RespuestaConsultaTrackId")]
    public class ConsultaResultadoResponse
    {
        [XmlElement("trackId")]
        [JsonPropertyName("trackId")]
        public string TrackId { get; set; }

        [XmlElement("codigo")]
        [JsonPropertyName("codigo")]
        public int Codigo { get; set; }

        [XmlElement("estado")]
        [JsonPropertyName("estado")]
        public string Estado { get; set; }

        [XmlElement("rnc")]
        [JsonPropertyName("rnc")]
        public string Rnc { get; set; }

        [XmlElement("encf")]
        [JsonPropertyName("encf")]
        public string ENcf { get; set; }

        [XmlElement("secuenciaUtilizada")]
        [JsonPropertyName("secuenciaUtilizada")]
        public bool SecuenciaUtilizada { get; set; }

        [XmlElement("fechaRecepcion")]
        [JsonPropertyName("fechaRecepcion")]
        public string FechaRecepcion { get; set; }

        [XmlArray("mensajes")]
        [XmlArrayItem("MensajeCodigo")]
        [JsonPropertyName("mensajes")]
        public List<MensajeCodigo> Mensajes { get; set; }
    }

    public class ConsultaEstadoResponse
    {
        [JsonPropertyName("codigo")]
        public int Codigo { get; set; }

        [JsonPropertyName("estado")]
        public string Estado { get; set; }

        [JsonPropertyName("rncEmisor")]
        public string RncEmisor { get; set; }

        [JsonPropertyName("ncfElectronico")]
        public string NcfElectronico { get; set; }

        [JsonPropertyName("montoTotal")]
        public decimal MontoTotal { get; set; }

        [JsonPropertyName("totalITBIS")]
        public decimal TotalITBIS { get; set; }

        [JsonPropertyName("fechaEmision")]
        public string FechaEmision { get; set; }

        [JsonPropertyName("fechaFirma")]
        public string FechaFirma { get; set; }

        [JsonPropertyName("rncComprador")]
        public string RncComprador { get; set; }

        [JsonPropertyName("codigoSeguridad")]
        public string CodigoSeguridad { get; set; }

        [JsonPropertyName("idExtranjero")]
        public string IdExtranjero { get; set; }
    }

    public class RfceConsultaResponse
    {
        [JsonPropertyName("rnc")]
        public string Rnc { get; set; }

        [JsonPropertyName("encf")]
        public string ENcf { get; set; }

        [JsonPropertyName("secuenciaUtilizada")]
        public bool SecuenciaUtilizada { get; set; }

        [JsonPropertyName("codigo")]
        public string Codigo { get; set; }

        [JsonPropertyName("estado")]
        public string Estado { get; set; }

        [JsonPropertyName("mensajes")]
        public List<MensajeCodigo> Mensajes { get; set; }
    }

    public class TimbreResponse
    {
        public string RncEmisor { get; set; }
        public string RazonSocialEmisor { get; set; }
        public string RncComprador { get; set; }
        public string RazonSocialComprador { get; set; }
        public string ENcf { get; set; }
        public string FechaEmision { get; set; }
        public decimal TotalITBIS { get; set; }
        public decimal MontoTotal { get; set; }
        public string Estado { get; set; }
    }

    public class TimbreFcResponse
    {
        public string RncEmisor { get; set; }
        public string RazonSocialEmisor { get; set; }
        public string ENcf { get; set; }
        public string Estado { get; set; }
    }

    public class MensajeCodigo
    {
        public string Codigo { get; set; }
        public string Valor { get; set; }
    }

    public class DirectorioContribuyente
    {
        public string Nombre { get; set; }
        public string Rnc { get; set; }
        public string UrlRecepcion { get; set; }
        public string UrlAceptacion { get; set; }
        public string UrlOpcional { get; set; }
    }

    public class EstatusServicio
    {
        public string Servicio { get; set; }
        public string Estatus { get; set; }
        public string Ambiente { get; set; }
    }

    public class TrackIdDetalle
    {
        public string TrackId { get; set; }
        public string Estado { get; set; }
        public string FechaRecepcion { get; set; }
    }

    public class VentanaMantenimiento
    {
        public DateTime Inicio { get; set; }
        public DateTime Fin { get; set; }
        public string Descripcion { get; set; }
    }

    public class AprobacionComercialResponse
    {
        [JsonPropertyName("codigo")]
        public string Codigo { get; set; }

        [JsonPropertyName("estado")]
        public string Estado { get; set; }

        [JsonPropertyName("mensaje")]
        public List<string> Mensaje { get; set; }
    }

    public class AnulacionResponse
    {
        [JsonPropertyName("rnc")]
        public string Rnc { get; set; }

        [JsonPropertyName("codigo")]
        public string Codigo { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("mensajes")]
        public List<string> Mensajes { get; set; }
    }

    public class TimbreEcfRequest
    {
        public string RncEmisor { get; set; }
        public string RncComprador { get; set; }
        public string ENcf { get; set; }
        public string FechaEmision { get; set; }
        public decimal MontoTotal { get; set; }
        public string FechaFirma { get; set; }
        public string CodigoSeguridad { get; set; }
    }

    public class TimbreFcRequest
    {
        public string RncEmisor { get; set; }
        public string ENcf { get; set; }
        public decimal MontoTotal { get; set; }
        public string CodigoSeguridad { get; set; }
    }

    public class ConsultaEstadoRequest
    {
        public string RncEmisor { get; set; }
        public string ENcf { get; set; }
        public string RncComprador { get; set; }
        public string CodigoSeguridad { get; set; }

        public ConsultaEstadoRequest(string rncEmisor, string eNcf, string rncComprador = null, string codigoSeguridad = null)
        {
            RncEmisor = rncEmisor;
            ENcf = eNcf;
            RncComprador = rncComprador;
            CodigoSeguridad = codigoSeguridad;
        }
    }
}
