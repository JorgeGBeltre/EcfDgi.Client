using System;
using System.Collections.Generic;

using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    public class EcfEnvironmentConfig
    {
        public string AutenticacionUrl { get; set; }
        public string RecepcionUrl { get; set; }
        public string RecepcionFcUrl { get; set; }
        public string ConsultaResultadoUrl { get; set; }
        public string ConsultaEstadoUrl { get; set; }
        public string ConsultaTrackIdsUrl { get; set; }
        public string ConsultaRfceUrl { get; set; }
        public string AprobacionComercialUrl { get; set; }
        public string AnulacionRangosUrl { get; set; }
        public string DirectorioUrl { get; set; }
        public string TimbreUrl { get; set; }
        public string TimbreFcUrl { get; set; }
        public string EstatusServiciosUrl { get; set; } = "https://statusecf.dgii.gov.do";

        public static EcfEnvironmentConfig GetConfig(AmbienteEnum ambiente)
        {
            return ambiente switch
            {
                AmbienteEnum.PreCertificacion => new EcfEnvironmentConfig
                {
                    AutenticacionUrl = "https://ecf.dgii.gov.do/testecf/autenticacion",
                    RecepcionUrl = "https://ecf.dgii.gov.do/testecf/recepcion",
                    RecepcionFcUrl = "https://fc.dgii.gov.do/testecf/recepcionfc",
                    ConsultaResultadoUrl = "https://ecf.dgii.gov.do/testecf/consultaresultado",
                    ConsultaEstadoUrl = "https://ecf.dgii.gov.do/testecf/consultaestado",
                    ConsultaTrackIdsUrl = "https://ecf.dgii.gov.do/testecf/consultatrackids",
                    ConsultaRfceUrl = "https://fc.dgii.gov.do/testecf/consultarfce",
                    AprobacionComercialUrl = "https://ecf.dgii.gov.do/testecf/aprobacioncomercial",
                    AnulacionRangosUrl = "https://ecf.dgii.gov.do/testecf/anulacionrangos",
                    DirectorioUrl = "https://ecf.dgii.gov.do/testecf/consultadirectorio",
                    TimbreUrl = "https://ecf.dgii.gov.do/testecf/consultatimbre",
                    TimbreFcUrl = "https://fc.dgii.gov.do/testecf/consultatimbrefc"
                },
                AmbienteEnum.Certificacion => new EcfEnvironmentConfig
                {
                    AutenticacionUrl = "https://ecf.dgii.gov.do/certecf/autenticacion",
                    RecepcionUrl = "https://ecf.dgii.gov.do/certecf/recepcion",
                    RecepcionFcUrl = "https://fc.dgii.gov.do/certecf/recepcionfc",
                    ConsultaResultadoUrl = "https://ecf.dgii.gov.do/certecf/consultaresultado",
                    ConsultaEstadoUrl = "https://ecf.dgii.gov.do/certecf/consultaestado",
                    ConsultaTrackIdsUrl = "https://ecf.dgii.gov.do/certecf/consultatrackids",
                    ConsultaRfceUrl = "https://fc.dgii.gov.do/certecf/consultarfce",
                    AprobacionComercialUrl = "https://ecf.dgii.gov.do/certecf/aprobacioncomercial",
                    AnulacionRangosUrl = "https://ecf.dgii.gov.do/certecf/anulacionrangos",
                    DirectorioUrl = "https://ecf.dgii.gov.do/certecf/consultadirectorio",
                    TimbreUrl = "https://ecf.dgii.gov.do/certecf/consultatimbre",
                    TimbreFcUrl = "https://fc.dgii.gov.do/certecf/consultatimbrefc"
                },
                AmbienteEnum.Produccion => new EcfEnvironmentConfig
                {
                    AutenticacionUrl = "https://ecf.dgii.gov.do/ecf/autenticacion",
                    RecepcionUrl = "https://ecf.dgii.gov.do/ecf/recepcion",
                    RecepcionFcUrl = "https://fc.dgii.gov.do/ecf/recepcionfc",
                    ConsultaResultadoUrl = "https://ecf.dgii.gov.do/ecf/consultaresultado",
                    ConsultaEstadoUrl = "https://ecf.dgii.gov.do/ecf/consultaestado",
                    ConsultaTrackIdsUrl = "https://ecf.dgii.gov.do/ecf/consultatrackids",
                    ConsultaRfceUrl = "https://fc.dgii.gov.do/ecf/consultarfce",
                    AprobacionComercialUrl = "https://ecf.dgii.gov.do/ecf/aprobacioncomercial",
                    AnulacionRangosUrl = "https://ecf.dgii.gov.do/ecf/anulacionrangos",
                    DirectorioUrl = "https://ecf.dgii.gov.do/ecf/consultadirectorio",
                    TimbreUrl = "https://ecf.dgii.gov.do/ecf/consultatimbre",
                    TimbreFcUrl = "https://fc.dgii.gov.do/ecf/consultatimbrefc"
                },
                _ => throw new ArgumentException("Ambiente no soportado")
            };
        }
    }
}
