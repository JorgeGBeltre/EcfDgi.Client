using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Serialization;

namespace EcfDgii.Client.Domain.Entities
{
    [XmlRoot("RFCE")]
    public class Rfce
    {
        [XmlElement("Encabezado")]
        public RfceEncabezado Encabezado { get; set; } = new RfceEncabezado();

        [XmlAnyElement("Signature", Namespace = "http://www.w3.org/2000/09/xmldsig#")]
        public XmlElement? Signature { get; set; }
    }

    public class RfceEncabezado
    {
        [XmlElement("VERSIÓN")]
        public string Version { get; set; } = "1.0";

        [XmlElement("IdDoc")]
        public RfceIdDoc IdDoc { get; set; } = new RfceIdDoc();

        [XmlElement("Emisor")]
        public RfceEmisor Emisor { get; set; } = new RfceEmisor();

        [XmlElement("Comprador")]
        public RfceComprador Comprador { get; set; }

        [XmlElement("Totales")]
        public RfceTotales Totales { get; set; } = new RfceTotales();
    }

    public class RfceIdDoc
    {
        public string TipoeCF { get; set; } = "32";
        public string ENcf { get; set; }
        public int TipoIngresos { get; set; }
        public int TipoPago { get; set; }

        [XmlArray("TablaFormasPago")]
        [XmlArrayItem("FormaDePago")]
        public List<FormaDePagoItem> TablaFormasPago { get; set; } = new List<FormaDePagoItem>();
    }

    public class FormaDePagoItem
    {
        public int FormaPago { get; set; }
        public decimal MontoPago { get; set; }
    }

    public class RfceEmisor
    {
        public string RncEmisor { get; set; }
        public string RazonSocialEmisor { get; set; }
        public string FechaEmision { get; set; }
    }

    public class RfceComprador
    {
        public string RncComprador { get; set; }
        public string IdentificadorExtranjero { get; set; }
        public string RazonSocialComprador { get; set; }
    }

    public class RfceTotales
    {
        public decimal? MontoGravadoTotal { get; set; }
        public decimal? MontoGravadoI1 { get; set; }
        public decimal? MontoGravadoI2 { get; set; }
        public decimal? MontoGravadoI3 { get; set; }
        public decimal? MontoExento { get; set; }
        public decimal? TotalITBIS { get; set; }
        public decimal? TotalITBIS1 { get; set; }
        public decimal? TotalITBIS2 { get; set; }
        public decimal? TotalITBIS3 { get; set; }
        public decimal? MontoImpuestoAdicional { get; set; }

        [XmlArray("ImpuestosAdicionales")]
        [XmlArrayItem("ImpuestoAdicional")]
        public List<ImpuestoAdicionalItem> ImpuestosAdicionales { get; set; } = new List<ImpuestoAdicionalItem>();

        public decimal MontoTotal { get; set; }
        public decimal? MontoNoFacturable { get; set; }
        public decimal? MontoPeriodo { get; set; }

        public string CodigoSeguridadeCF { get; set; }
    }

    public class ImpuestoAdicionalItem
    {
        public string TipoImpuesto { get; set; }
        public decimal? MontoImpuestoSelectivoConsumoEspecifico { get; set; }
        public decimal? MontoImpuestoSelectivoConsumoAdvalorem { get; set; }
        public decimal? OtrosImpuestosAdicionales { get; set; }
    }
}
