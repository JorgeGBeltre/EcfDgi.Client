# EcfDgi.Client SDK - Facturación Electrónica República Dominicana

Este SDK proporciona una integración robusta y simplificada con los servicios de Facturación Electrónica (e-CF) de la **Dirección General de Impuestos Internos (DGII)** de la República Dominicana. Diseñado bajo principios de Clean Architecture, permite la generación, firma digital, envío y consulta de comprobantes fiscales electrónicos de forma eficiente.

## Table of Contents
- [Descripción Técnica](#descripción-técnica)
  - [Flujo de Funcionamiento](#flujo-de-funcionamiento)
  - [Autenticación y Seguridad](#autenticación-y-seguridad)
- [Gestión de Credenciales](#gestión-de-credenciales)
  - [Estructura de Directorios](#estructura-de-directorios)
- [Formatos de Datos](#formatos-de-datos)
  - [Ejemplo JSON (Pre-envío)](#ejemplo-json-pre-envío)
  - [Ejemplo XML (Firmado)](#ejemplo-xml-firmado)
  - [Respuestas de la DGII](#respuestas-de-la-dgii)
- [Métodos Principales](#métodos-principales)
- [Configuración de Entorno](#configuración-de-entorno)
- [Instalación](#instalación)
- [Ejemplo de Uso Mínimo](#ejemplo-de-uso-mínimo)
- [Seguridad](#seguridad)
- [Licencia](#-licencia)
- [Contacto](#-contacto)
- [Soporte](#-soporte)


---

## Descripción Técnica

El SDK actúa como una capa de abstracción sobre los servicios SOAP/REST de la DGII. Maneja internamente la complejidad de la firma digital XMLDSig, la gestión de tokens de acceso y la serialización de documentos.

### Flujo de Funcionamiento (Diagrama ASCII)

```text
+-----------+       +-------------------+       +-------------------+
|  Tu App   | ----> |   EcfDgii SDK     | ----> |   Servidor DGII   |
+-----------+       +-------------------+       +-------------------+
      |                       |                           |
      | 1. Generar Factura    |                           |
      |---------------------->|                           |
      |                       | 2. Autenticación (Auth)   |
      |                       |-------------------------->|
      |                       |<--------------------------|
      |                       |    Token de Acceso        |
      |                       |                           |
      |                       | 3. Firmar XML (P12/PFX)   |
      |                       |------------------|        |
      |                       |                  |        |
      |                       |<-----------------|        |
      |                       |                           |
      |                       | 4. Envío de e-CF          |
      |                       |-------------------------->|
      |                       |<--------------------------|
      |                       |    TrackId / Respuesta    |
      | 5. Resultado Final    |                           |
      |<----------------------|                           |
```

### Autenticación y Seguridad
1. **Firma de Semilla**: El SDK solicita una "semilla" (seed) a la DGII, la firma con el certificado digital del contribuyente y la envía para obtener un **Token de Acceso**.
2. **Ciclo de Vida del Token**: El SDK gestiona automáticamente la expiración y renovación del token durante la sesión de envío.
3. **Firma Digital**: Todos los documentos XML son firmados utilizando el estándar **XMLDSig (Enveloped Signature)** con algoritmos SHA-256.

---

## Gestión de Credenciales

El SDK requiere un certificado digital de persona física (emitido por entidades como Avansi o BHD) en formato `.p12` o `.pfx`.

### Estructura de Directorios Recomendada
Se recomienda centralizar las credenciales fuera del acceso público del servidor web:

```text
/root-project
│
├── /config
│   └── /credenciales
│       ├── certificado_dgii.p12  <-- Certificado Digital
│       └── password.txt          <-- (Opcional) Pin/Password cifrado
│
├── .env                          <-- Configuración de rutas
└── ...
```

---

## Formatos de Datos

### Ejemplo JSON (Pre-envío)
Estructura simplificada de una factura electrónica (e-CF) antes de ser procesada por el SDK.

```json
{
  "Encabezado": {
    "IdDoc": {
      "TipoeCF": 31,
      "ENcf": "E310000000001",
      "FechaEmision": "2023-10-27"
    },
    "Emisor": {
      "RncEmisor": "123456789",
      "RazonSocialEmisor": "Mi Empresa S.R.L",
      "DireccionEmisor": "Av. Winston Churchill"
    },
    "Comprador": {
      "RncComprador": "987654321",
      "RazonSocialComprador": "Cliente Ejemplo"
    },
    "Totales": {
      "MontoTotal": 1180.00,
      "MontoITBIS": 180.00
    }
  },
  "Detalles": [
    {
      "NombreItem": "Servicio de Consultoría",
      "CantidadItem": 1,
      "PrecioUnitarioItem": 1000.00,
      "MontoItem": 1000.00
    }
  ]
}
```

### Ejemplo XML (Firmado)
El SDK transforma el JSON a un XML conforme al esquema XSD de la DGII e incluye la firma.

```xml
<eCF xmlns="http://dgii.gov.do/eCF">
  <Encabezado>...</Encabezado>
  <Detalles>...</Detalles>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <SignedInfo>...</SignedInfo>
    <SignatureValue>MIIEpAIBAAKCAQEA...</SignatureValue>
    <KeyInfo>
      <X509Data>
        <X509Certificate>MIIF...</X509Certificate>
      </X509Data>
    </KeyInfo>
  </Signature>
</eCF>
```

### Respuestas de la DGII

#### Éxito (JSON)
```json
{
  "trackId": "f9b8c7d6-e5f4-4321-a1b2-c3d4e5f6g7h8",
  "codigo": "0",
  "mensaje": "Archivo recibido correctamente",
  "fechaRecepcion": "2023-10-27T10:00:00Z"
}
```

#### Error (Rechazo)
```json
{
  "codigo": "12",
  "mensaje": "Error de validación de firma",
  "detalles": [
    {
      "codigoError": "SIG01",
      "descripcion": "El certificado ha expirado o no es válido para este RNC"
    }
  ]
}
```

---

## Métodos Principales

| Método | Descripción |
| :--- | :--- |
| `SendEcfAsync(xml, filename)` | Envía un documento e-CF ya serializado y firmado. |
| `SendRfceAsync(rfceModel)` | Procesa, firma y envía un Resumen de Factura de Consumo (RFCE). |
| `ConsultarResultadoAsync(trackId)` | Consulta el estatus de procesamiento de un envío previo. |
| `ConsultarEstadoAsync(rnc, encf)` | Verifica el estatus actual de un e-CF específico en los servidores DGII. |
| `ValidarTimbreEcfAsync(request)` | Valida la integridad de un timbre electrónico. |

---

## Configuración de Entorno

Crea un archivo `.env` en la raíz de tu proyecto para configurar el SDK de forma dinámica.

```env
# Configuración DGII
DGI_RNC_EMISOR=123456789
DGI_ENVIRONMENT=Homologacion # Homologacion o Produccion
DGI_CERT_PATH=C:/config/credenciales/mi_certificado.p12
DGI_CERT_PASSWORD=TuPasswordSeguro123

# Opciones del SDK
DGI_AUTO_RETRY_SEQUENCE=true
```

---

## Instalación

### .NET (NuGet)
```bash
dotnet add package EcfDgii.Client
```

---

## Ejemplo de Uso Mínimo

```csharp
using EcfDgii.Client;

// 1. Configurar opciones desde variables de entorno
var options = new EcfClientOptions {
    RncEmisor = Environment.GetEnvironmentVariable("DGI_RNC_EMISOR"),
    CertificatePath = Environment.GetEnvironmentVariable("DGI_CERT_PATH"),
    CertificatePassword = Environment.GetEnvironmentVariable("DGI_CERT_PASSWORD"),
    Environment = AmbienteEnum.Homologacion
};

// 2. Inicializar el cliente
var client = new EcfClient(options);

// 3. Preparar el contenido (XML o Objeto)
string xmlContent = File.ReadAllText("factura.xml");
string fileName = "101672957E3100000001.xml";

// 4. Enviar a DGII
try {
    var response = await client.SendEcfAsync(xmlContent, fileName);
    Console.WriteLine($"Envío exitoso. TrackId: {response.TrackId}");
} catch (EcfException ex) {
    Console.WriteLine($"Error DGII: {ex.Message}");
}
```

---

## Seguridad

> [!IMPORTANT]
> El manejo del certificado digital y la llave privada es responsabilidad crítica del integrador.

- **Almacenamiento**: Nunca guardes el certificado `.p12` en directorios accesibles vía HTTP.
- **Cifrado**: Si almacenas la contraseña en archivos de configuración, asegúrate de utilizar mecanismos de cifrado de secretos (como Azure Key Vault o AWS Secrets Manager).
- **Vigencia**: Monitorea la fecha de expiración de tu certificado para evitar interrupciones en el servicio.

---

##  Licencia

Esta biblioteca está bajo la **MIT License**. Consulte [LICENSE](LICENSE) para obtener más detalles.

---


## Contacto

Autor: **Jorge Gaspar Beltre Rivera**  
Project: **EcfDgii.Client**

 [![GitHub](https://img.shields.io/badge/GitHub-181717?style=for-the-badge&logo=github&logoColor=white)](https://github.com/JorgeGBeltre)
 [![Linkedln](https://img.shields.io/badge/LinkedIn-0A66C2?style=for-the-badge&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/jorge-gaspar-beltre-rivera/)
 [![Email](https://img.shields.io/badge/Email-EA4335?style=for-the-badge&logo=gmail&logoColor=white)](mailto:Jorgegaspar3021@gmail.com)

---

## Soporte

Este proyecto es desarrollado de forma independiente.

Incluso una pequeña contribución me ayuda a dedicar más tiempo al desarrollo, pruebas y lanzamiento de nuevas características.

 [![Buy Me a Coffee](https://img.shields.io/badge/Buy_Me_a_Coffee-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://www.paypal.com/donate/?hosted_button_id=2VLA8BWT967LU)