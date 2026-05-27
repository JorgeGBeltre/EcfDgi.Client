> # EcfDgi.Client SDK - Dominican Republic Electronic Invoicing

> **Note:** The SDK name appears to be "EcfDgii.Client" in the text, but the title says "EcfDgi.Client". I've kept the original as written, but there may be a typo.

This SDK provides a robust and simplified integration with the Electronic Invoicing (e-CF) services of the **General Directorate of Internal Taxes (DGII)** of the Dominican Republic. Designed under Clean Architecture principles, it enables the generation, digital signing, sending, and querying of electronic tax receipts efficiently.

## Table of Contents
- [Technical Description](#technical-description)
  - [Operational Flow](#operational-flow)
  - [Authentication and Security](#authentication-and-security)
- [Credential Management](#credential-management)
  - [Directory Structure](#directory-structure)
- [Data Formats](#data-format)
  - [JSON Example (Pre-Submission)](#json-example-pre-submission)
  - [XML Example (Signed)](#xml-example-signed)
  - [DGII Responses](#dgii-responses)
- [Main Methods](#main-methods)
- [Environment Configuration](#environment-configuration)
- [Installation](#installation)
- [Minimal Usage Example](#minimal-usage-example)
- [Security](#security)
- [License](#-license)
- [Contact](#-contact)
- [Support](#-support)


---

## Technical Description

The SDK acts as an abstraction layer over the DGII's SOAP/REST services. It internally handles the complexity of XMLDSig digital signing, access token management, and document serialization.

### Operational Flow (ASCII Diagram)

```text
+-----------+       +-------------------+       +-------------------+
|  Your App | ----> |   EcfDgii SDK     | ----> |   DGII Server     |
+-----------+       +-------------------+       +-------------------+
      |                       |                           |
      | 1. Generate Invoice   |                           |
      |---------------------->|                           |
      |                       | 2. Authentication (Auth)  |
      |                       |-------------------------->|
      |                       |<--------------------------|
      |                       |    Access Token           |
      |                       |                           |
      |                       | 3. Sign XML (P12/PFX)     |
      |                       |------------------|        |
      |                       |                  |        |
      |                       |<-----------------|        |
      |                       |                           |
      |                       | 4. e-CF Submission        |
      |                       |-------------------------->|
      |                       |<--------------------------|
      |                       |    TrackId / Response     |
      | 5. Final Result       |                           |
      |<----------------------|                           |
```

### Authentication and Security
1. **Seed Signing**: The SDK requests a "seed" from the DGII, signs it with the taxpayer's digital certificate, and sends it to obtain an **Access Token**.
2. **Token Lifecycle**: The SDK automatically manages token expiration and renewal during the submission session.
3. **Digital Signature**: All XML documents are signed using the **XMLDSig (Enveloped Signature)** standard with SHA-256 algorithms.

---

## Credential Management

The SDK requires an individual digital certificate (issued by entities such as Avansi or BHD) in `.p12` or `.pfx` format.

### Recommended Directory Structure
It is recommended to centralize credentials outside the public access of the web server:

```text
/root-project
│
├── /config
│   └── /credentials
│       ├── dgii_certificate.p12  <-- Digital Certificate
│       └── password.txt          <-- (Optional) Encrypted Pin/Password
│
├── .env                          <-- Path configuration
└── ...
```

---

## Data Formats

### JSON Example (Pre-Submission)
Simplified structure of an electronic invoice (e-CF) before being processed by the SDK.

```json
{
  "Header": {
    "DocId": {
      "eCFType": 31,
      "ENcf": "E310000000001",
      "IssueDate": "2023-10-27"
    },
    "Issuer": {
      "IssuerRnc": "123456789",
      "IssuerBusinessName": "My Company S.R.L",
      "IssuerAddress": "Av. Winston Churchill"
    },
    "Buyer": {
      "BuyerRnc": "987654321",
      "BuyerBusinessName": "Example Client"
    },
    "Totals": {
      "TotalAmount": 1180.00,
      "ITBISTotal": 180.00
    }
  },
  "Details": [
    {
      "ItemName": "Consulting Service",
      "ItemQuantity": 1,
      "ItemUnitPrice": 1000.00,
      "ItemAmount": 1000.00
    }
  ]
}
```

### XML Example (Signed)
The SDK transforms the JSON into XML according to the DGII's XSD schema and includes the signature.

```xml
<eCF xmlns="http://dgii.gov.do/eCF">
  <Header>...</Header>
  <Details>...</Details>
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

### DGII Responses

#### Success (JSON)
```json
{
  "trackId": "f9b8c7d6-e5f4-4321-a1b2-c3d4e5f6g7h8",
  "code": "0",
  "message": "File received successfully",
  "receiptDate": "2023-10-27T10:00:00Z"
}
```

#### Error (Rejection)
```json
{
  "code": "12",
  "message": "Signature validation error",
  "details": [
    {
      "errorCode": "SIG01",
      "description": "The certificate has expired or is not valid for this RNC"
    }
  ]
}
```

---

## Main Methods

| Method | Description |
| :--- | :--- |
| `SendEcfAsync(xml, filename)` | Sends an already serialized and signed e-CF document. |
| `SendRfceAsync(rfceModel)` | Processes, signs, and sends a Consumption Invoice Summary (RFCE). |
| `QueryResultAsync(trackId)` | Queries the processing status of a previous submission. |
| `QueryStatusAsync(rnc, encf)` | Checks the current status of a specific e-CF on DGII servers. |
| `ValidateEcfStampAsync(request)` | Validates the integrity of an electronic stamp. |

---

## Environment Configuration

Create a `.env` file in your project root to configure the SDK dynamically.

```env
# DGII Configuration
DGI_ISSUER_RNC=123456789
DGI_ENVIRONMENT=Homologacion # Homologacion or Produccion
DGI_CERT_PATH=C:/config/credentials/my_certificate.p12
DGI_CERT_PASSWORD=YourSecurePassword123

# SDK Options
DGI_AUTO_RETRY_SEQUENCE=true
```

---

## Installation

### .NET (NuGet)
```bash
dotnet add package EcfDgii.Client
```

---

## Minimal Usage Example

```csharp
using EcfDgii.Client;

// 1. Configure options from environment variables
var options = new EcfClientOptions {
    IssuerRnc = Environment.GetEnvironmentVariable("DGI_ISSUER_RNC"),
    CertificatePath = Environment.GetEnvironmentVariable("DGI_CERT_PATH"),
    CertificatePassword = Environment.GetEnvironmentVariable("DGI_CERT_PASSWORD"),
    Environment = AmbienteEnum.Homologacion
};

// 2. Initialize the client
var client = new EcfClient(options);

// 3. Prepare content (XML or Object)
string xmlContent = File.ReadAllText("invoice.xml");
string fileName = "101672957E3100000001.xml";

// 4. Send to DGII
try {
    var response = await client.SendEcfAsync(xmlContent, fileName);
    Console.WriteLine($"Successful submission. TrackId: {response.TrackId}");
} catch (EcfException ex) {
    Console.WriteLine($"DGII Error: {ex.Message}");
}
```

---

## Security

> [!IMPORTANT]
> The handling of the digital certificate and private key is the integrator's critical responsibility.

- **Storage**: Never save the `.p12` certificate in directories accessible via HTTP.
- **Encryption**: If you store the password in configuration files, make sure to use secret encryption mechanisms (such as Azure Key Vault or AWS Secrets Manager).
- **Validity**: Monitor your certificate's expiration date to avoid service interruptions.

---

## License

This library is under the **MIT License**. See [LICENSE](LICENSE) for more details.

---

## Contact

Author: **Jorge Gaspar Beltre Rivera**  
Project: **EcfDgii.Client**

 [![GitHub](https://img.shields.io/badge/GitHub-181717?style=for-the-badge&logo=github&logoColor=white)](https://github.com/JorgeGBeltre)
 [![Linkedln](https://img.shields.io/badge/LinkedIn-0A66C2?style=for-the-badge&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/jorge-gaspar-beltre-rivera/)
 [![Email](https://img.shields.io/badge/Email-EA4335?style=for-the-badge&logo=gmail&logoColor=white)](mailto:Jorgegaspar3021@gmail.com)

---

## Support

This project is developed independently.

Even a small contribution helps me dedicate more time to development, testing, and launching new features.

 [![Buy Me a Coffee](https://img.shields.io/badge/Buy_Me_a_Coffee-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://www.paypal.com/donate/?hosted_button_id=2VLA8BWT967LU)
