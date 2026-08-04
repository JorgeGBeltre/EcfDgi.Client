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
        [XmlElement("Version")]
        public string Version { get; set; } = "1.0";

        [XmlElement("IdDoc")]
        public RfceIdDoc IdDoc { get; set; } = new RfceIdDoc();

        [XmlElement("Emisor")]
        public RfceEmisor Emisor { get; set; } = new RfceEmisor();

        [XmlElement("Comprador")]
        public RfceComprador? Comprador { get; set; } = new RfceComprador();

        [XmlElement("Totales")]
        public RfceTotales Totales { get; set; } = new RfceTotales();

        [XmlElement("CodigoSeguridadeCF")]
        public string? CodigoSeguridadeCF { get; set; }
    }

    public class RfceIdDoc
    {
        public string TipoeCF { get; set; } = "32";
        
        [XmlElement("eNCF")]
        public string ENcf { get; set; } = string.Empty;
        
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
        [XmlElement("RNCEmisor")]
        public string RncEmisor { get; set; } = string.Empty;

        [XmlElement("RazonSocialEmisor")]
        public string RazonSocialEmisor { get; set; } = string.Empty;

        [XmlElement("FechaEmision")]
        public string FechaEmision { get; set; } = string.Empty;
    }

    public class RfceComprador
    {
        [XmlElement("RNCComprador")]
        public string? RncComprador { get; set; }

        [XmlElement("IdentificadorExtranjero")]
        public string? IdentificadorExtranjero { get; set; }

        [XmlElement("RazonSocialComprador")]
        public string? RazonSocialComprador { get; set; }
    }

    public class RfceTotales
    {
        public decimal? MontoGravadoTotal { get; set; }
        [XmlIgnore] public bool MontoGravadoTotalSpecified => MontoGravadoTotal.HasValue;

        public decimal? MontoGravadoI1 { get; set; }
        [XmlIgnore] public bool MontoGravadoI1Specified => MontoGravadoI1.HasValue;

        public decimal? MontoGravadoI2 { get; set; }
        [XmlIgnore] public bool MontoGravadoI2Specified => MontoGravadoI2.HasValue;

        public decimal? MontoGravadoI3 { get; set; }
        [XmlIgnore] public bool MontoGravadoI3Specified => MontoGravadoI3.HasValue;

        public decimal? MontoExento { get; set; }
        [XmlIgnore] public bool MontoExentoSpecified => MontoExento.HasValue;

        public decimal? TotalITBIS { get; set; }
        [XmlIgnore] public bool TotalITBISSpecified => TotalITBIS.HasValue;

        public decimal? TotalITBIS1 { get; set; }
        [XmlIgnore] public bool TotalITBIS1Specified => TotalITBIS1.HasValue;

        public decimal? TotalITBIS2 { get; set; }
        [XmlIgnore] public bool TotalITBIS2Specified => TotalITBIS2.HasValue;

        public decimal? TotalITBIS3 { get; set; }
        [XmlIgnore] public bool TotalITBIS3Specified => TotalITBIS3.HasValue;

        public decimal? MontoImpuestoAdicional { get; set; }
        [XmlIgnore] public bool MontoImpuestoAdicionalSpecified => MontoImpuestoAdicional.HasValue;

        [XmlArray("ImpuestosAdicionales")]
        [XmlArrayItem("ImpuestoAdicional")]
        public List<ImpuestoAdicionalItem> ImpuestosAdicionales { get; set; } = new List<ImpuestoAdicionalItem>();

        public decimal MontoTotal { get; set; }

        public decimal? MontoNoFacturable { get; set; }
        [XmlIgnore] public bool MontoNoFacturableSpecified => MontoNoFacturable.HasValue;

        public decimal? MontoPeriodo { get; set; }
        [XmlIgnore] public bool MontoPeriodoSpecified => MontoPeriodo.HasValue;
    }

    public class ImpuestoAdicionalItem
    {
        public string? TipoImpuesto { get; set; }

        public decimal? MontoImpuestoSelectivoConsumoEspecifico { get; set; }
        [XmlIgnore] public bool MontoImpuestoSelectivoConsumoEspecificoSpecified => MontoImpuestoSelectivoConsumoEspecifico.HasValue;

        public decimal? MontoImpuestoSelectivoConsumoAdvalorem { get; set; }
        [XmlIgnore] public bool MontoImpuestoSelectivoConsumoAdvaloremSpecified => MontoImpuestoSelectivoConsumoAdvalorem.HasValue;

        public decimal? OtrosImpuestosAdicionales { get; set; }
        [XmlIgnore] public bool OtrosImpuestosAdicionalesSpecified => OtrosImpuestosAdicionales.HasValue;
    }
}
