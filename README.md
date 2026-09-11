# EcfDgii.Client API & SDK — Dominican Republic Electronic Invoicing

[![.NET 10](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/download)
[![EF Core 10](https://img.shields.io/badge/EF%20Core-10.0-purple)](https://learn.microsoft.com/en-us/ef/core/)
[![PostgreSQL](https://img.shields.io/badge/Database-PostgreSQL-blue)](https://www.postgresql.org/)
[![MediatR](https://img.shields.io/badge/CQRS-MediatR-orange)](https://github.com/jbogard/MediatR)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/JorgeGBeltre/EcfDgii.Client)

---

**EcfDgii.Client** is an enterprise-grade solution that wraps and exposes the Dominican Republic Tax Authority (**DGII**) Comprobante Fiscal Electronico (**e-CF**) SOAP and REST integration services. Built under **Clean Architecture** and **Domain-Driven Design (DDD)** principles, this solution provides a robust REST API wrapper, secure JWT-based authentication, PostgreSQL persistence with automated auditing and soft-delete, pre-transmission local XSD schema validation across all 15 official DGII document schemas, W3C XMLDSig digital signing, multi-environment routing (Test, Certification, Production), correlation logging, and complete Docker orchestration.

---

## Table of Contents

- [What's New in Refactored v2.0.0](#whats-new-in-refactored-v200)
- [Architectural Overview](#architectural-overview)
- [Core Features & Capabilities](#core-features--capabilities)
- [DGII Transmission Workflows](#dgii-transmission-workflows)
  - [Tax Credit Invoices (e-CF 31)](#tax-credit-invoices-e-cf-31)
  - [Consumer Invoices (e-CF 32 & RFCE 32)](#consumer-invoices-e-cf-32--rfce-32)
  - [Credit Notes (e-CF 34)](#credit-notes-e-cf-34)
  - [Debit Notes (e-CF 33)](#debit-notes-e-cf-33)
  - [Informal Vendor & Purchasing Bills (e-CF 41)](#informal-vendor--purchasing-bills-e-cf-41)
  - [Minor Expense Receipts (e-CF 43)](#minor-expense-receipts-e-cf-43)
  - [Special Tax Regime Receipts (e-CF 44)](#special-tax-regime-receipts-e-cf-44)
  - [Governmental Receipts (e-CF 45)](#governmental-receipts-e-cf-45)
  - [Export Receipts (e-CF 46)](#export-receipts-e-cf-46)
  - [Foreign Payments (e-CF 47)](#foreign-payments-e-cf-47)
  - [Commercial Reception & Approval (ARECF & ACECF)](#commercial-reception--approval-arecf--acecf)
  - [Sequence Annulment (ANECF)](#sequence-annulment-anecf)
- [Canonical Ingestion Model (JSON)](#canonical-ingestion-model-json)
  - [CanonicalDocumentDto Schema](#canonicaldocumentdto-schema)
  - [JSON Example: Tax Credit Invoice (E31)](#json-example-tax-credit-invoice-e31)
  - [JSON Example: Consumer Invoice (E32)](#json-example-consumer-invoice-e32)
  - [JSON Example: Credit Note (E34)](#json-example-credit-note-e34)
  - [JSON Example: Purchase Bill with Withholding (E41)](#json-example-purchase-bill-with-withholding-e41)
  - [JSON Example: Payments Abroad with ISR Withholding (E47)](#json-example-payments-abroad-with-isr-withholding-e47)
  - [JSON Example: Zero-Rated Export Invoice (E46)](#json-example-zero-rated-export-invoice-e46)
- [Signed XML Specifications & Schema Reference](#signed-xml-specifications--schema-reference)
  - [The 15 Official DGII XSD Schemas](#the-15-official-dgii-xsd-schemas)
  - [XML Structure: Factura de Credito Fiscal (e-CF 31)](#xml-structure-factura-de-credito-fiscal-e-cf-31)
  - [XML Structure: Nota de Credito (e-CF 34)](#xml-structure-nota-de-credito-e-cf-34)
  - [XML Structure: Comprobante de Compras (e-CF 41)](#xml-structure-comprobante-de-compras-e-cf-41)
  - [XML Structure: Consumer Summary (RFCE 32)](#xml-structure-consumer-summary-rfce-32)
  - [XML Structure: Commercial Acknowledgment (ARECF)](#xml-structure-commercial-acknowledgment-arecf)
  - [XML Structure: Commercial Approval (ACECF)](#xml-structure-commercial-approval-acecf)
  - [XML Structure: Sequence Annulment (ANECF)](#xml-structure-sequence-annulment-anecf)
- [Step-by-Step DGII Client Replication Guide](#step-by-step-dgii-client-replication-guide)
  - [Step 1: Mutual TLS and Seed-Based Authentication](#step-1-mutual-tls-and-seed-based-authentication)
  - [Step 2: Canonical XML Generation & Strict Formatting Rules](#step-2-canonical-xml-generation--strict-formatting-rules)
  - [Step 3: W3C XMLDSig Digital Signature Implementation](#step-3-w3c-xmldsig-digital-signature-implementation)
  - [Step 4: Security Code (CodigoSeguridad) Calculation](#step-4-security-code-codigoseguridad-calculation)
  - [Step 5: QR Code & Timbre URL Generation](#step-5-qr-code--timbre-url-generation)
  - [Step 6: Pre-Transmission Local XSD Validation Gate](#step-6-pre-transmission-local-xsd-validation-gate)
  - [Step 7: Transmission Pathways (Asynchronous vs Synchronous)](#step-7-transmission-pathways-asynchronous-vs-synchronous)
  - [Step 8: Multi-Tenant Sequence Allocation & Concurrency Control](#step-8-multi-tenant-sequence-allocation--concurrency-control)
  - [Step 9: Document State Machine & Retry Mechanics](#step-9-document-state-machine--retry-mechanics)
  - [Step 10: Environment Configuration & Endpoint Catalog](#step-10-environment-configuration--endpoint-catalog)
- [REST API Endpoints Reference](#rest-api-endpoints-reference)
- [Solution Structure](#solution-structure)
- [Installation & Setup](#installation--setup)
- [Dependencies](#dependencies)
- [Configuration & Options](#configuration--options)
- [Database Persistence & Migrations](#database-persistence--migrations)
- [Docker Orchestration](#docker-orchestration)
- [Diagnostics & Testing](#diagnostics--testing)
- [License](#license)
- [Contact](#contact)
- [Support](#support)

---

## What's New in Refactored v2.0.0

This release completes the transformation from a single SDK library into a multi-tenant enterprise billing platform built with ASP.NET Core 10, Entity Framework Core, PostgreSQL, and CQRS via MediatR.

### Security Enhancements

| Area | Legacy SDK Limitation | v2.0.0 Enterprise Solution |
|---|---|---|
| **API Authentication** | Endpoints were unprotected and could be invoked anonymously. | Secure JWT Bearer Token validation using `Microsoft.AspNetCore.Authentication.JwtBearer` with role-based policies. |
| **Password Hashing** | Plaintext or reversible user passwords. | BCrypt-based one-way salting and hashing via `BCrypt.Net-Next`. |
| **Certificate Handling** | Certificate path and passwords were hardcoded in code files. | Strongly-typed options bound via `IOptions<EcfClientOptions>` and `IOptions<EcfEmisorOptions>`. |
| **Token Caching** | Token requested on every call, overloading DGII authentication servers. | Distributed token cache using Redis or local memory with proactive renewal (5-minute safety margin) and reactive 401 invalidation. |

### Architectural Enhancements

| Area | Legacy SDK Limitation | v2.0.0 Enterprise Solution |
|---|---|---|
| **Clean Architecture** | Monolithic project where UI, controllers, business rules, and SDK logic were tightly coupled. | Clean Architecture separation into `Domain`, `Application`, `Infrastructure`, `Shared`, and `Api`. |
| **Canonical Ingestion** | Tightly bound to proprietary ERP database formats. | Universal `CanonicalDocumentDto` supporting Invoices, Credit Notes, Debit Notes, and Bills from any ERP or accounting system. |
| **Pre-Transmission XSD Gate**| Sent invalid XML directly to DGII, receiving opaque HTTP 400 errors and wasting sequence numbers. | In-memory XSD validation against all 15 official DGII schemas before any network transmission occurs. |
| **Concurrency & Idempotency**| Concurrent requests could allocate duplicate sequence numbers or duplicate submissions. | PostgreSQL unique constraint `uq_ecf_documents_tenant_source_txn` with automatic transaction collision recovery. |

### Robustness & Persistence Enhancements

| Area | Legacy SDK Limitation | v2.0.0 Enterprise Solution |
|---|---|---|
| **Local Persistence** | In-memory tracking lost upon application restart. | PostgreSQL persistence tracking every submitted e-CF, trackId, security code, and XML content. |
| **Auditing & Soft Delete**| No audit logs; permanent row deletion. | Base entity `AuditableEntity` auto-populates `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, and soft-delete filters. |
| **Standardized Error Handling**| Unhandled exceptions leaked internal stack traces. | Standardized RFC 9457 `ProblemDetails` middleware formatting. |

---

## Architectural Overview

The `EcfDgii.Client` solution sits between upstream ERP systems (e.g., QuickBooks Desktop, SAP, Odoo, custom POS systems) and the Dominican Republic Tax Authority (DGII).

```text
+-------------------------------------------------------------------------------+
|                             Upstream ERP Systems                              |
|           (QuickBooks Desktop, SAP, Odoo, Custom Web/Mobile POS)             |
+-------------------------------------------------------------------------------+
                                        |
                           POST /api/documents (JSON)
                                        v
+-------------------------------------------------------------------------------+
|                            EcfDgii.Client.Api                                 |
|   +-----------------------------------------------------------------------+   |
|   | Controller Layer: DocumentsController, EcfController, AuthController   |   |
|   +-----------------------------------------------------------------------+   |
|                                       |                                       |
|                                       v                                       |
|   +-----------------------------------------------------------------------+   |
|   | Application Layer: CQRS Handlers, Canonical Normalization, DTOs       |   |
|   +-----------------------------------------------------------------------+   |
|                                       |                                       |
|                                       v                                       |
|   +-----------------------------------------------------------------------+   |
|   | Infrastructure Layer:                                                 |   |
|   |  - Sequence Manager (PostgreSQL Atomic Reservation)                   |   |
|   |  - XML Builder (Strict XSD tag ordering, no empty tags)               |   |
|   |  - XML Signer (W3C XMLDSig, RSA-SHA256, Enveloped, C14N)              |   |
|   |  - In-Memory Schema Validator (15 DGII XSD Schemas)                   |   |
|   |  - Security Code & QR Timbre Generator                                |   |
|   |  - DGII Direct Transport (Multipart HTTP Client & Token Manager)      |   |
|   +-----------------------------------------------------------------------+   |
+-------------------------------------------------------------------------------+
                  |                                             |
     Asynchronous REST (Multipart)                 Synchronous FC (Multipart)
                  |                                             |
                  v                                             v
+-----------------------------------+         +---------------------------------+
| DGII Electronic Invoicing Service |         | DGII Consumer Reception Service |
| (ecf.dgii.gov.do/recepcion)       |         | (fc.dgii.gov.do/recepcionfc)    |
| - e-CF 31, 33, 34, 41, 43-47      |         | - e-CF 32 (< RD$ 250,000)       |
| - e-CF 32 (>= RD$ 250,000)        |         | - RFCE 32 Consumer Summaries    |
+-----------------------------------+         +---------------------------------+
```

---

## Core Features & Capabilities

- **Universal Document Support**: Native generation, validation, signing, and dispatching for all 10 electronic fiscal receipt types (E31, E32, E33, E34, E41, E43, E44, E45, E46, E47), consumer summaries (RFCE 32), commercial acknowledgments (ARECF), commercial approvals (ACECF), and sequence cancellations (ANECF).
- **In-Memory Pre-Transmission Validation**: Evaluates the signed XML against the official DGII XSD definition in memory. Prevents invalid payloads from reaching DGII, saving network roundtrips and protecting sequential e-NCF ranges from being consumed by syntax errors.
- **W3C Compliant XMLDSig**: Canonicalizes documents using inclusive C14N (`http://www.w3.org/TR/2001/REC-xml-c14n-20010315`), signs using RSA-SHA256 (`http://www.w3.org/2001/04/xmldsig-more#rsa-sha256`), and packages public X.509 certificate data in the `<KeyInfo>` node.
- **Official Security Code Calculation**: Computes the 6-character hexadecimal verification code directly from the SHA-256 digest of the signature value (`SHA256(UTF8(SignatureValue))`).
- **Dynamic QR Code URLs**: Generates compliant DGII QR code inspection URLs for both standard tax receipts (`/consultatimbre`) and consumer invoices (`/consultatimbrefc`).
- **Intelligent State Machine**: Manages document lifecycles through deterministic states: `SequenceAllocated`, `AwaitingTransmission`, `Signed`, `RejectedByDgii`, `Uncertain`, `SigningFailed`, `SchemaInvalid`, and `Unsigned`.
- **Atomic Concurrency Control**: Handles race conditions when upstream ERPs send concurrent duplicate requests for the same transaction ID via PostgreSQL unique constraint isolation.
- **Deterministic Retry Protocol**: If a transmission fails with network timeout or HTTP 5xx, the document enters the `Uncertain` state. The client enforces a 2-minute cooldown before querying DGII via `/api/consultas/estado` to prevent duplicate submissions.
- **Peer-to-Peer B2B Endpoints**: Implements buyer reception endpoints (`/fe/recepcion/api/ecf`), automatic ARECF acknowledgment generation, and commercial approval exchange (`/fe/aprobacioncomercial/api/ecf`).

---

## DGII Transmission Workflows

The DGII defines distinct transmission protocols depending on the fiscal receipt type, transaction amount, and operational context.

### Tax Credit Invoices (e-CF 31)

Used for transactions between commercial entities where the buyer requires tax credit (ITBIS) and cost deduction for corporate income tax (ISR).

- **Buyer Identification**: Mandatory. Must contain an active 9-digit RNC or 11-digit Cedula in `<RNCComprador>`.
- **Sequence Expiration**: Mandatory `<FechaVencimientoSecuencia>`.
- **Income Type**: Mandatory `<TipoIngresos>` (typically `01` for operational income).
- **Transmission Method**: Sent via HTTP `POST` to the Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`) using `multipart/form-data` with Bearer token authentication.
- **DGII Response**: Returns an asynchronous `trackId`. The client polls the status endpoint (`/api/consultas/estado?trackid={trackId}`) until the state reaches `Aceptado` or `Rechazado`.

### Consumer Invoices (e-CF 32 & RFCE 32)

Issued to end consumers for personal consumption.

- **Amount Under RD$ 250,000.00**:
  - Buyer identification is optional.
  - Can be transmitted in real time via the Synchronous Consumer Reception endpoint (`https://fc.dgii.gov.do/{ambiente}/recepcionfc/api/recepcion/ecf`) or batched into an electronic consumer summary (**RFCE 32**).
  - No `<FechaVencimientoSecuencia>` and no `<IndicadorNotaCredito>` allowed in the schema.
- **Amount Equal to or Greater than RD$ 250,000.00**:
  - Buyer identification is **strictly mandatory** per DGII regulations (`<RNCComprador>` with 9 or 11 digits, or `<IdentificadorExtranjero>` for foreign tourists).
  - Must be transmitted individually to the Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`), exactly like an e-CF 31.

### Credit Notes (e-CF 34)

Used to reduce amounts, correct items, or fully annul previously issued electronic invoices.

- **Sequence Expiration**: Must **NOT** contain `<FechaVencimientoSecuencia>`. Including this tag causes immediate XSD rejection.
- **Credit Note Indicator**: Mandatory `<IndicadorNotaCredito>`:
  - `0`: Issued within 30 calendar days of the modified document (retains ITBIS recovery rights).
  - `1`: Issued after 30 calendar days (no ITBIS recovery rights).
- **Reference Section**: Mandatory `<InformacionReferencia>`:
  - `<NCFModificado>`: The affected e-NCF (e.g., `E310000000150` or legacy NCF like `B0100000001`).
  - `<FechaNCFModificado>`: Emission date of the affected document in `dd-MM-yyyy` format.
  - `<CodigoModificacion>`: Modification code (`1` = Full annulment, `2` = Text correction, `3` = Amount correction).
  - `<RazonModificacion>`: Explanation of the correction (up to 90 characters).
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Debit Notes (e-CF 33)

Issued to recover expenses, interest, or unbilled charges associated with a previously issued invoice.

- **Sequence Expiration**: Mandatory `<FechaVencimientoSecuencia>`.
- **Reference Section**: Mandatory `<InformacionReferencia>` with `<NCFModificado>`, `<FechaNCFModificado>`, and `<CodigoModificacion>`.
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Informal Vendor & Purchasing Bills (e-CF 41)

Issued when a taxpayer purchases goods or services from individuals or informal vendors who are not registered as electronic billing issuers. The issuer acts as a withholding agent.

- **Income Type**: Prohibited. Must **NOT** contain `<TipoIngresos>`.
- **Vendor Identification**: The informal supplier's RNC or Cedula is placed in `<RNCComprador>`.
- **Withholding Block**: Mandatory `<Retencion>` inside each `<Item>` and summary withholding inside `<Totales>`:
  - `<IndicadorAgenteRetencionoPercepcion>`: Value `1` (Withholding agent).
  - `<MontoITBISRetenido>`: Total ITBIS withheld.
  - `<MontoISRRetenido>`: Total Income Tax (ISR) withheld (mandatory when purchasing services, `<IndicadorBienoServicio>` = `2`).
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Minor Expense Receipts (e-CF 43)

Used to consolidate miscellaneous operational expenses (toll fees, public parking, casual office supplies).

- **Income Type**: Prohibited in `<IdDoc>`.
- **Buyer Node**: The entire `<Comprador>` element is **strictly prohibited** and must be completely omitted from the XML.
- **Tax Rules**: Exempt from ITBIS. Must declare `<MontoExento>` and `<MontoTotal>`. `<MontoGravadoTotal>` and `<TotalITBIS>` are forbidden.
- **Discounts**: `<DescuentoMonto>` is forbidden in `<Item>`.
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Special Tax Regime Receipts (e-CF 44)

Issued to entities operating under tax-exempt regimes (Free Trade Zones / Zonas Francas under Law 8-90, Diplomatic missions).

- **Tax Rules**: Invoices are exempt from ITBIS (`<MontoExento>` and `<MontoTotal>` only).
- **Foreign Identification**: Buyer identification can be an RNC or `<IdentificadorExtranjero>`.
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Governmental Receipts (e-CF 45)

Issued for billing goods and services to Dominican State institutions and public dependencies.

- **Buyer Identification**: Mandatory `<RNCComprador>` with a valid government institution RNC.
- **Sequence Expiration**: Mandatory `<FechaVencimientoSecuencia>`.
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Export Receipts (e-CF 46)

Issued for international sales of goods and services outside the Dominican Republic.

- **Income Type**: Mandatory `<TipoIngresos>` set to `02` (Income from exports).
- **Buyer Identification**: `<IdentificadorExtranjero>` or foreign corporate registration.
- **Tax Rules**: Subject to 0% ITBIS rate under Slot 3:
  - `<MontoGravadoTotal>` and `<MontoGravadoI3>` populated with taxable base.
  - `<ITBIS3>0</ITBIS3>`
  - `<TotalITBIS>0.00</TotalITBIS>` and `<TotalITBIS3>0.00</TotalITBIS3>`
  - Line `<IndicadorFacturacion>` set to `3` (Zero rate).
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Foreign Payments (e-CF 47)

Issued when making payments to non-resident entities or individuals abroad for services rendered or intellectual property royalties.

- **Income Type**: Prohibited in `<IdDoc>`.
- **Buyer Node**: `<RNCComprador>` is omitted. Only `<IdentificadorExtranjero>` and `<RazonSocialComprador>` are allowed.
- **Tax Rules**: ITBIS exempt (`<MontoExento>`). Mandatory ISR withholding (`<TotalISRRetencion>` and line `<MontoISRRetenido>`).
- **Line Rules**: Line `<IndicadorBienoServicio>` set to `2` (Services). `<DescuentoMonto>` is prohibited.
- **Transmission Method**: Asynchronous Reception endpoint (`/recepcion/api/facturaselectronicas`).

### Commercial Reception & Approval (ARECF & ACECF)

In B2B transactions, both the issuer and receiver exchange commercial conformity messages:

```text
[ Issuer (Emisor) ]                                [ Receiver (Receptor) ]
         |                                                    |
         | -------- 1. Transmit e-CF (XML signed) ----------> |
         |                                                    |
         | <------- 2. Reply ARECF (Acuse de Recibo) -------- |
         |             (Signed XML, Estado = 0)               |
         |                                                    |
         | <------- 3. Post ACECF (Aprobacion Comercial) ---- |
         |             (Signed XML, Estado = 1 or 2)          |
```

1. **ARECF (Acuse de Recibo)**: Returned immediately upon receiving the e-CF XML. Contains `RNCEmisor`, `RNCComprador`, `eNCF`, `Estado = 0` (Received), and reception timestamp. Signed by the receiver.
2. **ACECF (Aprobación Comercial)**: Transmitted after verifying physical or commercial delivery of goods/services. `Estado = 1` (Accepted) or `Estado = 2` (Rejected). Signed by the receiver and delivered to the issuer's registered endpoint and to DGII.

### Sequence Annulment (ANECF)

Used to notify the DGII of unused e-NCF ranges that are being cancelled due to system failure, misallocation, or closure.

- Contains ranges defined by `<SecuenciaDesde>` and `<SecuenciaHasta>`.
- Signed and sent to `https://ecf.dgii.gov.do/{ambiente}/anulacionrangos/api/operaciones/anularrango`.

---

## Canonical Ingestion Model (JSON)

The API exposes a clean, jurisdiction-neutral JSON contract (`CanonicalDocumentDto`) that allows any billing or ERP platform to submit documents without having to learn the DGII's XML schema.

### CanonicalDocumentDto Schema

```json
{
  "ncf": "string (optional - legacy NCF reference)",
  "documentKind": "Invoice | CreditNote | DebitNote | Bill",
  "tipoComprobante": "E31 | E32 | E33 | E34 | E41 | E43 | E44 | E45 | E46 | E47",
  "sourceReference": {
    "provider": "QuickBooksDesktop | SAP | CustomERP",
    "txnId": "string (mandatory unique ERP transaction identifier)",
    "editSequence": "string (optional ERP revision timestamp)"
  },
  "header": {
    "rncEmisor": "string (9 or 11 digits)",
    "razonSocialEmisor": "string (legal company name)",
    "rncComprador": "string (9 or 11 digits, or foreign passport)",
    "razonSocialComprador": "string (customer name)",
    "fechaEmision": "YYYY-MM-DD or DD-MM-YYYY"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "string (product name)",
      "quantity": 1.00,
      "unitPrice": 1000.00,
      "amount": 1000.00
    }
  ],
  "totals": {
    "montoSubtotal": 1000.00,
    "montoGravadoTotal": 1000.00,
    "montoExento": 0.00,
    "taxBuckets": [
      {
        "rate": 18,
        "base": 1000.00,
        "tax": 180.00
      }
    ],
    "montoItbis": 180.00,
    "montoTotal": 1180.00
  },
  "references": {
    "correctsTxnId": "string (optional ERP reference)",
    "correctsENcf": "string (mandatory for E33 and E34)",
    "codigoModificacion": 1,
    "razonModificacion": "string (up to 90 chars)",
    "fechaNcfModificado": "YYYY-MM-DD"
  },
  "retention": {
    "indicadorAgenteRetencionoPercepcion": 1,
    "montoItbisRetenido": 180.00,
    "montoIsrRetenido": 100.00
  }
}
```

### JSON Example: Tax Credit Invoice (E31)

```json
{
  "sourceReference": {
    "provider": "QuickBooksDesktop",
    "txnId": "QB-INV-2026-9001",
    "editSequence": "1726050000"
  },
  "documentKind": "Invoice",
  "tipoComprobante": "E31",
  "header": {
    "rncComprador": "101000021",
    "razonSocialComprador": "CONSTRUCTORA NACIONAL SAS",
    "fechaEmision": "2026-09-11"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "Cemento Gris Portland 50kg",
      "quantity": 100.00,
      "unitPrice": 450.00,
      "amount": 45000.00
    },
    {
      "lineNumber": 2,
      "itemName": "Varilla Corrugada 1/2 pulgada",
      "quantity": 50.00,
      "unitPrice": 380.00,
      "amount": 19000.00
    }
  ],
  "totals": {
    "montoGravadoTotal": 64000.00,
    "montoExento": 0.00,
    "taxBuckets": [
      {
        "rate": 18,
        "base": 64000.00,
        "tax": 11520.00
      }
    ],
    "montoItbis": 11520.00,
    "montoTotal": 75520.00
  }
}
```

### JSON Example: Consumer Invoice (E32)

```json
{
  "sourceReference": {
    "provider": "CustomPOS",
    "txnId": "POS-TICKET-88219",
    "editSequence": "1726051200"
  },
  "documentKind": "Invoice",
  "tipoComprobante": "E32",
  "header": {
    "rncComprador": "",
    "razonSocialComprador": "Consumidor Final",
    "fechaEmision": "2026-09-11"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "Pintura Acrilica Blanca Galon",
      "quantity": 2.00,
      "unitPrice": 1250.00,
      "amount": 2500.00
    }
  ],
  "totals": {
    "montoGravadoTotal": 2500.00,
    "montoExento": 0.00,
    "taxBuckets": [
      {
        "rate": 18,
        "base": 2500.00,
        "tax": 450.00
      }
    ],
    "montoItbis": 450.00,
    "montoTotal": 2950.00
  }
}
```

### JSON Example: Credit Note (E34)

```json
{
  "sourceReference": {
    "provider": "QuickBooksDesktop",
    "txnId": "QB-CRN-4001",
    "editSequence": "1726052000"
  },
  "documentKind": "CreditNote",
  "tipoComprobante": "E34",
  "header": {
    "rncComprador": "101000021",
    "razonSocialComprador": "CONSTRUCTORA NACIONAL SAS",
    "fechaEmision": "2026-09-11"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "Devolucion de Cemento Gris Portland 50kg",
      "quantity": 10.00,
      "unitPrice": 450.00,
      "amount": 4500.00
    }
  ],
  "totals": {
    "montoGravadoTotal": 4500.00,
    "montoExento": 0.00,
    "taxBuckets": [
      {
        "rate": 18,
        "base": 4500.00,
        "tax": 810.00
      }
    ],
    "montoItbis": 810.00,
    "montoTotal": 5310.00
  },
  "references": {
    "correctsENcf": "E310000000150",
    "codigoModificacion": 3,
    "razonModificacion": "Devolucion por material defectuoso",
    "fechaNcfModificado": "2026-09-10"
  }
}
```

### JSON Example: Purchase Bill with Withholding (E41)

```json
{
  "sourceReference": {
    "provider": "QuickBooksDesktop",
    "txnId": "QB-BILL-7701",
    "editSequence": "1726053000"
  },
  "documentKind": "Bill",
  "tipoComprobante": "E41",
  "header": {
    "rncComprador": "00112345678",
    "razonSocialComprador": "JUAN PEREZ REPARACIONES",
    "fechaEmision": "2026-09-11"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "Servicio de Reparacion Electrica de Planta",
      "quantity": 1.00,
      "unitPrice": 20000.00,
      "amount": 20000.00
    }
  ],
  "totals": {
    "montoGravadoTotal": 20000.00,
    "montoExento": 0.00,
    "taxBuckets": [
      {
        "rate": 18,
        "base": 20000.00,
        "tax": 3600.00
      }
    ],
    "montoItbis": 3600.00,
    "montoTotal": 23600.00
  },
  "retention": {
    "indicadorAgenteRetencionoPercepcion": 1,
    "montoItbisRetenido": 3600.00,
    "montoIsrRetenido": 2000.00
  }
}
```

### JSON Example: Payments Abroad with ISR Withholding (E47)

```json
{
  "sourceReference": {
    "provider": "SAP",
    "txnId": "SAP-PAY-INT-5002",
    "editSequence": "1726054000"
  },
  "documentKind": "Bill",
  "tipoComprobante": "E47",
  "header": {
    "rncComprador": "US-EIN-987654321",
    "razonSocialComprador": "CLOUD HOSTING SOLUTIONS LLC",
    "fechaEmision": "2026-09-11"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "Suscripcion Servidores en la Nube Anual",
      "quantity": 1.00,
      "unitPrice": 150000.00,
      "amount": 150000.00
    }
  ],
  "totals": {
    "montoSubtotal": 150000.00,
    "montoExento": 150000.00,
    "montoItbis": 0.00,
    "montoTotal": 150000.00
  },
  "retention": {
    "indicadorAgenteRetencionoPercepcion": 1,
    "montoItbisRetenido": 0.00,
    "montoIsrRetenido": 40500.00
  }
}
```

### JSON Example: Zero-Rated Export Invoice (E46)

```json
{
  "sourceReference": {
    "provider": "ERPConnector",
    "txnId": "EXP-INV-10023",
    "editSequence": "1726055000"
  },
  "documentKind": "Invoice",
  "tipoComprobante": "E46",
  "header": {
    "rncComprador": "FL-CORP-441209",
    "razonSocialComprador": "MIAMI TRADING GROUP INC",
    "fechaEmision": "2026-09-11"
  },
  "lines": [
    {
      "lineNumber": 1,
      "itemName": "Cacao Organico Dominicano en Grano (Saco 50kg)",
      "quantity": 200.00,
      "unitPrice": 5000.00,
      "amount": 1000000.00
    }
  ],
  "totals": {
    "montoGravadoTotal": 1000000.00,
    "montoExento": 0.00,
    "taxBuckets": [
      {
        "rate": 0,
        "base": 1000000.00,
        "tax": 0.00
      }
    ],
    "montoItbis": 0.00,
    "montoTotal": 1000000.00
  }
}
```

---

## Signed XML Specifications & Schema Reference

### The 15 Official DGII XSD Schemas

The system bundles and validates against the complete suite of official DGII XML Schema Definitions (XSD):

| Document Code | Official XSD File Name | Purpose | Critical Schema Rules |
|---|---|---|---|
| **e-CF 31** | `e-CF 31 v.1.0.xsd` | Factura de Credito Fiscal | `RNCComprador` required (9 or 11 digits). `FechaVencimientoSecuencia` required. |
| **e-CF 32** | `e-CF 32 v.1.0.xsd` | Factura de Consumo | No `FechaVencimientoSecuencia`. No `IndicadorNotaCredito`. Buyer required if amount >= RD$250,000. |
| **e-CF 33** | `e-CF 33 v.1.0.xsd` | Nota de Debito | `InformacionReferencia` required (`NCFModificado`, `CodigoModificacion`). |
| **e-CF 34** | `e-CF 34 v.1.0.xsd` | Nota de Credito | No `FechaVencimientoSecuencia`. `IndicadorNotaCredito` (0 or 1) and `InformacionReferencia` required. |
| **e-CF 41** | `e-CF 41 v.1.0.xsd` | Comprobante de Compras | No `TipoIngresos`. Vendor in `RNCComprador`. Mandatory ITBIS/ISR `Retencion` block. |
| **e-CF 43** | `e-CF 43 v.1.0.xsd` | Gastos Menores | No `TipoIngresos`. `<Comprador>` tag forbidden. ITBIS exempt (`MontoExento`). No discounts. |
| **e-CF 44** | `e-CF 44 v.1.0.xsd` | Regimenes Especiales | ITBIS exempt. Supports foreign ID or RNC. |
| **e-CF 45** | `e-CF 45 v.1.0.xsd` | Gubernamental | State institution RNC required in `RNCComprador`. |
| **e-CF 46** | `e-CF 46 v.1.0.xsd` | Exportaciones | `TipoIngresos=02`. Zero-rated ITBIS bucket 3 (`MontoGravadoI3`, `ITBIS3=0`, line indicator `3`). |
| **e-CF 47** | `e-CF 47 v.1.0.xsd` | Pagos al Exterior | No `TipoIngresos`. No `RNCComprador`. Foreign ID only. Mandatory ISR withholding. Service indicator `2`. |
| **RFCE 32** | `RFCE 32 v.1.0.xsd` | Resumen Factura Consumo | Daily summary of consumer invoices under RD$ 250,000. Must include `CodigoSeguridadeCF`. |
| **ARECF** | `ARECF v1.0.xsd` | Acuse de Recibo Comercial | B2B receptor confirmation. Mandatory `Estado=0` and reception timestamp. |
| **ACECF** | `ACECF v.1.0.xsd` | Aprobacion Comercial | B2B buyer approval (`Estado=1` Accepted, `Estado=2` Rejected). |
| **ANECF** | `ANECF v.1.0.xsd` | Anulacion de e-NCF | Sequence range cancellation (`SecuenciaDesde` to `SecuenciaHasta`). |
| **Semilla** | `Semilla v.1.0.xsd` | Semilla de Autenticacion | Challenge seed for mutual authentication. |

### XML Structure: Factura de Credito Fiscal (e-CF 31)

```xml
<?xml version="1.0" encoding="utf-8"?>
<ECF>
  <Encabezado>
    <Version>1.0</Version>
    <IdDoc>
      <TipoeCF>31</TipoeCF>
      <eNCF>E310000000001</eNCF>
      <FechaVencimientoSecuencia>31-12-2027</FechaVencimientoSecuencia>
      <TipoIngresos>01</TipoIngresos>
      <TipoPago>1</TipoPago>
    </IdDoc>
    <Emisor>
      <RNCEmisor>101889063</RNCEmisor>
      <RazonSocialEmisor>WILLY CHIC DOMINICANA SRL</RazonSocialEmisor>
      <DireccionEmisor>Distrito Nacional, SD</DireccionEmisor>
      <FechaEmision>11-09-2026</FechaEmision>
    </Emisor>
    <Comprador>
      <RNCComprador>101000021</RNCComprador>
      <RazonSocialComprador>CONSTRUCTORA NACIONAL SAS</RazonSocialComprador>
    </Comprador>
    <Totales>
      <MontoGravadoTotal>64000.00</MontoGravadoTotal>
      <MontoGravadoI1>64000.00</MontoGravadoI1>
      <ITBIS1>18</ITBIS1>
      <TotalITBIS>11520.00</TotalITBIS>
      <TotalITBIS1>11520.00</TotalITBIS1>
      <MontoTotal>75520.00</MontoTotal>
    </Totales>
  </Encabezado>
  <DetallesItems>
    <Item>
      <NumeroLinea>1</NumeroLinea>
      <IndicadorFacturacion>1</IndicadorFacturacion>
      <NombreItem>Cemento Gris Portland 50kg</NombreItem>
      <IndicadorBienoServicio>1</IndicadorBienoServicio>
      <CantidadItem>100.00</CantidadItem>
      <PrecioUnitarioItem>450.00</PrecioUnitarioItem>
      <MontoItem>45000.00</MontoItem>
    </Item>
    <Item>
      <NumeroLinea>2</NumeroLinea>
      <IndicadorFacturacion>1</IndicadorFacturacion>
      <NombreItem>Varilla Corrugada 1/2 pulgada</NombreItem>
      <IndicadorBienoServicio>1</IndicadorBienoServicio>
      <CantidadItem>50.00</CantidadItem>
      <PrecioUnitarioItem>380.00</PrecioUnitarioItem>
      <MontoItem>19000.00</MontoItem>
    </Item>
  </DetallesItems>
  <FechaHoraFirma>11-09-2026 12:45:10</FechaHoraFirma>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <SignedInfo>
      <CanonicalizationMethod Algorithm="http://www.w3.org/TR/2001/REC-xml-c14n-20010315" />
      <SignatureMethod Algorithm="http://www.w3.org/2001/04/xmldsig-more#rsa-sha256" />
      <Reference URI="">
        <Transforms>
          <Transform Algorithm="http://www.w3.org/2000/09/xmldsig#enveloped-signature" />
        </Transforms>
        <DigestMethod Algorithm="http://www.w3.org/2001/04/xmlenc#sha256" />
        <DigestValue>5Z4xT...==</DigestValue>
      </Reference>
    </SignedInfo>
    <SignatureValue>k9aB...==</SignatureValue>
    <KeyInfo>
      <X509Data>
        <X509Certificate>MIIF...==</X509Certificate>
      </X509Data>
    </KeyInfo>
  </Signature>
</ECF>
```

### XML Structure: Nota de Credito (e-CF 34)

Notice that `<FechaVencimientoSecuencia>` is completely omitted, `<IndicadorNotaCredito>` is present inside `<IdDoc>`, and `<InformacionReferencia>` appears after `<DetallesItems>`.

```xml
<?xml version="1.0" encoding="utf-8"?>
<ECF>
  <Encabezado>
    <Version>1.0</Version>
    <IdDoc>
      <TipoeCF>34</TipoeCF>
      <eNCF>E340000000012</eNCF>
      <IndicadorNotaCredito>0</IndicadorNotaCredito>
      <TipoIngresos>01</TipoIngresos>
      <TipoPago>1</TipoPago>
    </IdDoc>
    <Emisor>
      <RNCEmisor>101889063</RNCEmisor>
      <RazonSocialEmisor>WILLY CHIC DOMINICANA SRL</RazonSocialEmisor>
      <DireccionEmisor>Distrito Nacional, SD</DireccionEmisor>
      <FechaEmision>11-09-2026</FechaEmision>
    </Emisor>
    <Comprador>
      <RNCComprador>101000021</RNCComprador>
      <RazonSocialComprador>CONSTRUCTORA NACIONAL SAS</RazonSocialComprador>
    </Comprador>
    <Totales>
      <MontoGravadoTotal>4500.00</MontoGravadoTotal>
      <MontoGravadoI1>4500.00</MontoGravadoI1>
      <ITBIS1>18</ITBIS1>
      <TotalITBIS>810.00</TotalITBIS>
      <TotalITBIS1>810.00</TotalITBIS1>
      <MontoTotal>5310.00</MontoTotal>
    </Totales>
  </Encabezado>
  <DetallesItems>
    <Item>
      <NumeroLinea>1</NumeroLinea>
      <IndicadorFacturacion>1</IndicadorFacturacion>
      <NombreItem>Devolucion de Cemento Gris Portland 50kg</NombreItem>
      <IndicadorBienoServicio>1</IndicadorBienoServicio>
      <CantidadItem>10.00</CantidadItem>
      <PrecioUnitarioItem>450.00</PrecioUnitarioItem>
      <MontoItem>4500.00</MontoItem>
    </Item>
  </DetallesItems>
  <InformacionReferencia>
    <NCFModificado>E310000000150</NCFModificado>
    <FechaNCFModificado>10-09-2026</FechaNCFModificado>
    <CodigoModificacion>3</CodigoModificacion>
    <RazonModificacion>Devolucion por material defectuoso</RazonModificacion>
  </InformacionReferencia>
  <FechaHoraFirma>11-09-2026 12:48:30</FechaHoraFirma>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <!-- Signature details -->
  </Signature>
</ECF>
```

### XML Structure: Comprobante de Compras (e-CF 41)

Notice that `<TipoIngresos>` is omitted from `<IdDoc>`, the informal supplier is in `<RNCComprador>`, `<Retencion>` is specified inside `<Item>`, and totals include withholding.

```xml
<?xml version="1.0" encoding="utf-8"?>
<ECF>
  <Encabezado>
    <Version>1.0</Version>
    <IdDoc>
      <TipoeCF>41</TipoeCF>
      <eNCF>E410000000005</eNCF>
      <FechaVencimientoSecuencia>31-12-2027</FechaVencimientoSecuencia>
      <TipoPago>1</TipoPago>
    </IdDoc>
    <Emisor>
      <RNCEmisor>101889063</RNCEmisor>
      <RazonSocialEmisor>WILLY CHIC DOMINICANA SRL</RazonSocialEmisor>
      <DireccionEmisor>Distrito Nacional, SD</DireccionEmisor>
      <FechaEmision>11-09-2026</FechaEmision>
    </Emisor>
    <Comprador>
      <RNCComprador>00112345678</RNCComprador>
      <RazonSocialComprador>JUAN PEREZ REPARACIONES</RazonSocialComprador>
    </Comprador>
    <Totales>
      <MontoGravadoTotal>20000.00</MontoGravadoTotal>
      <MontoGravadoI1>20000.00</MontoGravadoI1>
      <ITBIS1>18</ITBIS1>
      <TotalITBIS>3600.00</TotalITBIS>
      <TotalITBIS1>3600.00</TotalITBIS1>
      <MontoTotal>23600.00</MontoTotal>
      <TotalITBISRetenido>3600.00</TotalITBISRetenido>
      <TotalISRRetencion>2000.00</TotalISRRetencion>
    </Totales>
  </Encabezado>
  <DetallesItems>
    <Item>
      <NumeroLinea>1</NumeroLinea>
      <IndicadorFacturacion>1</IndicadorFacturacion>
      <Retencion>
        <IndicadorAgenteRetencionoPercepcion>1</IndicadorAgenteRetencionoPercepcion>
        <MontoITBISRetenido>3600.00</MontoITBISRetenido>
        <MontoISRRetenido>2000.00</MontoISRRetenido>
      </Retencion>
      <NombreItem>Servicio de Reparacion Electrica de Planta</NombreItem>
      <IndicadorBienoServicio>2</IndicadorBienoServicio>
      <CantidadItem>1.00</CantidadItem>
      <PrecioUnitarioItem>20000.00</PrecioUnitarioItem>
      <MontoItem>20000.00</MontoItem>
    </Item>
  </DetallesItems>
  <FechaHoraFirma>11-09-2026 12:50:00</FechaHoraFirma>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <!-- Signature details -->
  </Signature>
</ECF>
```

### XML Structure: Consumer Summary (RFCE 32)

```xml
<?xml version="1.0" encoding="utf-8"?>
<RFCE>
  <Encabezado>
    <Version>1.0</Version>
    <IdDoc>
      <RNCEmisor>101889063</RNCEmisor>
      <eNCF>E320000000010</eNCF>
      <FechaEmision>11-09-2026</FechaEmision>
    </IdDoc>
    <Totales>
      <MontoGravadoTotal>2500.00</MontoGravadoTotal>
      <MontoGravadoI1>2500.00</MontoGravadoI1>
      <TotalITBIS>450.00</TotalITBIS>
      <TotalITBIS1>450.00</TotalITBIS1>
      <MontoTotal>2950.00</MontoTotal>
    </Totales>
    <CodigoSeguridadeCF>4A7F2B</CodigoSeguridadeCF>
  </Encabezado>
  <FechaHoraFirma>11-09-2026 12:52:00</FechaHoraFirma>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <!-- Signature details -->
  </Signature>
</RFCE>
```

### XML Structure: Commercial Acknowledgment (ARECF)

```xml
<?xml version="1.0" encoding="utf-8"?>
<ARECF>
  <DetalleAcusedeRecibo>
    <Version>1.0</Version>
    <RNCEmisor>101889063</RNCEmisor>
    <RNCComprador>101000021</RNCComprador>
    <eNCF>E310000000001</eNCF>
    <Estado>0</Estado>
    <FechaHoraAcuseRecibo>11-09-2026 12:55:00</FechaHoraAcuseRecibo>
  </DetalleAcusedeRecibo>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <!-- Signature details -->
  </Signature>
</ARECF>
```

### XML Structure: Commercial Approval (ACECF)

```xml
<?xml version="1.0" encoding="utf-8"?>
<ACECF>
  <DetalleAprobacionComercial>
    <Version>1.0</Version>
    <RNCEmisor>101889063</RNCEmisor>
    <RNCComprador>101000021</RNCComprador>
    <eNCF>E310000000001</eNCF>
    <Estado>1</Estado>
    <FechaHoraAprobacionComercial>11-09-2026 13:10:00</FechaHoraAprobacionComercial>
  </DetalleAprobacionComercial>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <!-- Signature details -->
  </Signature>
</ACECF>
```

### XML Structure: Sequence Annulment (ANECF)

```xml
<?xml version="1.0" encoding="utf-8"?>
<ANECF>
  <DetalleAnulacion>
    <Version>1.0</Version>
    <RNCEmisor>101889063</RNCEmisor>
    <TipoeCF>31</TipoeCF>
    <CantidadeNCFAnulados>5</CantidadeNCFAnulados>
    <SecuenciaDesde>E310000000050</SecuenciaDesde>
    <SecuenciaHasta>E310000000054</SecuenciaHasta>
    <FechaHoraAnulacion>11-09-2026 13:15:00</FechaHoraAnulacion>
  </DetalleAnulacion>
  <Signature xmlns="http://www.w3.org/2000/09/xmldsig#">
    <!-- Signature details -->
  </Signature>
</ANECF>
```

---

## Step-by-Step DGII Client Replication Guide

If you are developing a DGII client in another language (Go, Python, Node.js, Java, Rust, or PHP), this section outlines every step required to build a compliant integration.

### Step 1: Mutual TLS and Seed-Based Authentication

All interactions with DGII require a dynamic JWT Bearer token obtained by signing an authentication seed.

```text
1. Client requests Seed:
   GET https://ecf.dgii.gov.do/{ambiente}/autenticacion/api/autenticacion/semilla
   Response: XML containing <SemillaModel><valor>{SEED_VALUE}</valor><fecha>{DATE}</fecha></SemillaModel>

2. Client digitally signs the seed:
   Sign the entire XML using your authorized digital certificate (PFX/P12) using XMLDSig Enveloped.

3. Client exchanges signed seed for JWT token:
   POST https://ecf.dgii.gov.do/{ambiente}/autenticacion/api/autenticacion/validarsemilla
   Content-Type: multipart/form-data
   Field: "xml", filename="semilla.xml", content=SIGNED_XML_STRING
   Response: XML containing <token>{JWT_TOKEN}</token><expira>{EXPIRATION_DATE}</expira>

4. Token Caching & Lifecycle:
   Cache the token. Proactively renew when remaining lifetime is <= 5 minutes.
   If any API returns HTTP 401 Unauthorized, invalidate the cache immediately and re-authenticate once.
```

### Step 2: Canonical XML Generation & Strict Formatting Rules

The DGII XML parser enforces rigid validation criteria:

1. **Tag Order is Invariant**: Child elements must appear in the exact order specified by the XSD `<xs:sequence>`. Placing `<RNCComprador>` after `<RazonSocialComprador>` causes immediate schema rejection.
2. **Never Emit Empty Tags**: If an optional element is null or empty, **omit the tag entirely**. Emitting `<Tag/>` or `<Tag></Tag>` violates pattern validations and triggers schema rejections.
3. **Strict Date Format**: All dates must be formatted as `dd-MM-yyyy` (matching regex `(3[01]|[12][0-9]|0?[1-9])-(1[012]|0?[1-9])-((19|20)\d{2})`). Convert any ISO `yyyy-MM-dd` incoming dates.
4. **Quantities Strictly Greater than Zero**: Line quantities must be positive (`Decimal18D1or2ValidationTypeMayorCero`). If a line represents a lump discount or adjustment with quantity 0, normalize it to `Quantity = 1.00` and `UnitPrice = Amount`.
5. **XML Character Escaping**: All text nodes must escape special characters:
   - `&` becomes `&amp;`
   - `<` becomes `&lt;`
   - `>` becomes `&gt;`
   - `"` becomes `&quot;`
   - `'` becomes `&apos;`
   - `©` becomes `&#169;`
   - `€` becomes `&#8364;`
   - `®` becomes `&#174;`
6. **Explicit ITBIS Buckets**:
   - `I1`: 18% rate -> `<MontoGravadoI1>`, `<ITBIS1>18</ITBIS1>`, `<TotalITBIS1>`
   - `I2`: 16% rate -> `<MontoGravadoI2>`, `<ITBIS2>16</ITBIS2>`, `<TotalITBIS2>`
   - `I3`: 0% rate (zero-rated export) -> `<MontoGravadoI3>`, `<ITBIS3>0</ITBIS3>`, `<TotalITBIS3>`
   - Exempt base -> `<MontoExento>`

### Step 3: W3C XMLDSig Digital Signature Implementation

Each electronic document must be signed according to the W3C XML Signature standard:

- **Canonicalization Method**: Inclusive C14N without comments: `http://www.w3.org/TR/2001/REC-xml-c14n-20010315`
- **Signature Method**: RSA with SHA-256: `http://www.w3.org/2001/04/xmldsig-more#rsa-sha256`
- **Digest Method**: SHA-256: `http://www.w3.org/2001/04/xmlenc#sha256`
- **Transform**: Enveloped signature: `http://www.w3.org/2000/09/xmldsig#enveloped-signature`
- **Reference URI**: Empty string `URI=""` (indicates the root element is signed).
- **Whitespace Handling**: `PreserveWhitespace = false` during DOM signature compilation.
- **KeyInfo**: Must contain `<X509Data><X509Certificate>` with the Base64-encoded DER representation of the certificate.
- **Insertion Location**: The resulting `<Signature>` block must be appended as the last child of the document root (immediately before `</ECF>`, `</RFCE>`, etc.).

### Step 4: Security Code (CodigoSeguridad) Calculation

The DGII requires a 6-character verification code printed on visual representations and included in RFCE summaries:

```text
1. Parse the signed XML and extract the inner text of <ds:SignatureValue>.
2. Trim leading and trailing whitespace from the string.
3. Convert the string to UTF-8 bytes:
   bytes = UTF8.GetBytes(signatureValue)
4. Compute the SHA-256 hash:
   hash = SHA256(bytes)
5. Convert hash bytes to a hexadecimal string:
   hexString = HexEncode(hash)   // e.g. "4a7f2b98e1c..."
6. Take the first 6 characters:
   codigoSeguridad = Substring(hexString, 0, 6).ToUpper()  // "4A7F2B"
```

### Step 5: QR Code & Timbre URL Generation

Visual receipts must include a QR code containing a validation URL:

- **Standard e-CF (Types 31, 33, 34, 41-47, and 32 >= RD$250k)**:
  ```text
  https://ecf.dgii.gov.do/{ambiente}/consultatimbre?rncemisor={RNCEmisor}&rnccomprador={RNCComprador}&encf={eNCF}&fechaemision={dd-MM-yyyy}&montototal={0.00}&fechafirma={dd-MM-yyyy HH:mm:ss}&codigoseguridad={CodigoSeguridad}
  ```
- **Consumer Invoices (e-CF 32 < RD$250k)**:
  ```text
  https://fc.dgii.gov.do/{ambiente}/consultatimbrefc?rncemisor={RNCEmisor}&encf={eNCF}&montototal={0.00}&codigoseguridad={CodigoSeguridad}
  ```

All parameter values must be URL-encoded (using `Uri.EscapeDataString`).

### Step 6: Pre-Transmission Local XSD Validation Gate

Before attempting any HTTP transmission to DGII:

1. Load all 15 DGII XSD schemas into an in-memory schema set.
2. Inspect the XML root name (`ECF`, `RFCE`, `ARECF`, `ACECF`, `ANECF`) and `<TipoeCF>` to select the appropriate XSD file.
3. Validate the **SIGNED** XML against the schema. (Validating unsigned XML will fail because the XSD schema ends with a mandatory `<xs:any>` element reserved for `<Signature>`).
4. If schema errors occur, reject the document locally and record errors. This saves sequence allocations and prevents DGII-side rejections.

### Step 7: Transmission Pathways (Asynchronous vs Synchronous)

```text
+-----------------------+-------------------------------------------------------+
| Document Category     | Destination & Method                                  |
+-----------------------+-------------------------------------------------------+
| Invoices E31, E33,    | Endpoint: POST /recepcion/api/facturaselectronicas    |
| E34, E41, E43-E47,    | Payload: multipart/form-data                          |
| and E32 >= RD$250k    | Header: Authorization: Bearer {Token}                 |
|                       | Response: {"trackId": "...", "error": null}           |
|                       | Next Step: Poll /api/consultas/estado?trackid={id}    |
+-----------------------+-------------------------------------------------------+
| Consumer E32          | Endpoint: POST /recepcionfc/api/recepcion/ecf         |
| (< RD$250,000)        | Payload: multipart/form-data                          |
| and RFCE 32           | Header: Authorization: Bearer {Token}                 |
|                       | Response: {"codigo": "0", "estado": "Aceptado"}       |
|                       | Next Step: Immediate synchronous acknowledgment       |
+-----------------------+-------------------------------------------------------+
```

### Step 8: Multi-Tenant Sequence Allocation & Concurrency Control

To guarantee zero duplicate e-NCFs and seamless recovery during race conditions:

1. Maintain sequence counters per tenant and comprobante type in PostgreSQL:
   ```sql
   UPDATE "EcfSequences"
   SET "CurrentValue" = "CurrentValue" + 1
   WHERE "TenantId" = @tenantId AND "TipoComprobante" = @tipo
   RETURNING "CurrentValue", "Prefix";
   ```
2. Format the e-NCF as `{Prefix}{CurrentValue.ToString().PadLeft(10, '0')}` (e.g., `E31` + `0000000001` = `E310000000001`).
3. Protect against concurrent duplicate submissions for the same ERP invoice via database unique index:
   ```sql
   CREATE UNIQUE INDEX uq_ecf_documents_tenant_source_txn 
   ON "EcfDocuments" ("TenantId", "SourceTxnId");
   ```
4. If a concurrent insert raises error `23505` (unique constraint violation), detach the failed insert and defer to the existing document record.

### Step 9: Document State Machine & Retry Mechanics

```text
[ Incoming Request ]
         |
         v
[ SequenceAllocated ] -----> (Signing Fails) -----> [ SigningFailed ] (Safe to retry)
         |
         v
[ AwaitingTransmission ] --> (XSD Invalid) -------> [ SchemaInvalid ] (Safe to retry)
         |
         v
[ Transmitting to DGII ]
         |
         +---- TrackId received ------> [ Signed ] (Confirmed by DGII)
         |
         +---- DGII Rejection --------> [ RejectedByDgii ] (Terminal)
         |
         +---- Timeout / HTTP 5xx ----> [ Uncertain ] (Cooldown 2 minutes)
                                              |
                                              v
                                   [ Reconcile with DGII ]
                                   - If found: [ Signed ]
                                   - If not found: Re-transmit under same eNCF
```

- **Safe-to-Retry States**: `SequenceAllocated`, `SigningFailed`, `SchemaInvalid`, `Unsigned`. If an ERP resubmits the same transaction ID in any of these states, reapply the payload and re-attempt signing without spending a new sequence number.
- **Uncertain State Cooldown**: If network interruption leaves a transmission uncertain, wait at least 2 minutes before querying `consultarEstado` to avoid false "Not Found" race conditions while DGII processes the file.

### Step 10: Environment Configuration & Endpoint Catalog

The DGII maintains three active environments:

| Service Function | Pre-Certification (Test) | Certification (Homologation) | Production |
|---|---|---|---|
| **Authentication** | `https://ecf.dgii.gov.do/testecf/autenticacion` | `https://ecf.dgii.gov.do/certecf/autenticacion` | `https://ecf.dgii.gov.do/ecf/autenticacion` |
| **Async Reception**| `https://ecf.dgii.gov.do/testecf/recepcion` | `https://ecf.dgii.gov.do/certecf/recepcion` | `https://ecf.dgii.gov.do/ecf/recepcion` |
| **Sync Consumer** | `https://fc.dgii.gov.do/testecf/recepcionfc` | `https://fc.dgii.gov.do/certecf/recepcionfc` | `https://fc.dgii.gov.do/ecf/recepcionfc` |
| **Status Polling** | `https://ecf.dgii.gov.do/testecf/consultaresultado`| `https://ecf.dgii.gov.do/certecf/consultaresultado`| `https://ecf.dgii.gov.do/ecf/consultaresultado` |
| **Document Query** | `https://ecf.dgii.gov.do/testecf/consultaestado` | `https://ecf.dgii.gov.do/certecf/consultaestado` | `https://ecf.dgii.gov.do/ecf/consultaestado` |
| **TrackId Query** | `https://ecf.dgii.gov.do/testecf/consultatrackids` | `https://ecf.dgii.gov.do/certecf/consultatrackids` | `https://ecf.dgii.gov.do/ecf/consultatrackids` |
| **RFCE Query** | `https://fc.dgii.gov.do/testecf/consultarfce` | `https://fc.dgii.gov.do/certecf/consultarfce` | `https://fc.dgii.gov.do/ecf/consultarfce` |
| **Commercial Appr**| `https://ecf.dgii.gov.do/testecf/aprobacioncomercial`| `https://ecf.dgii.gov.do/certecf/aprobacioncomercial`| `https://ecf.dgii.gov.do/ecf/aprobacioncomercial`|
| **Annulment** | `https://ecf.dgii.gov.do/testecf/anulacionrangos` | `https://ecf.dgii.gov.do/certecf/anulacionrangos` | `https://ecf.dgii.gov.do/ecf/anulacionrangos` |
| **Directory** | `https://ecf.dgii.gov.do/testecf/consultadirectorio`| `https://ecf.dgii.gov.do/certecf/consultadirectorio`| `https://ecf.dgii.gov.do/ecf/consultadirectorio`|
| **Timbre QR** | `https://ecf.dgii.gov.do/testecf/consultatimbre` | `https://ecf.dgii.gov.do/certecf/consultatimbre` | `https://ecf.dgii.gov.do/ecf/consultatimbre` |
| **Timbre FC QR** | `https://fc.dgii.gov.do/testecf/consultatimbrefc` | `https://fc.dgii.gov.do/certecf/consultatimbrefc` | `https://fc.dgii.gov.do/ecf/consultatimbrefc` |

---

## REST API Endpoints Reference

### Document Processing Endpoints (`/api/documents`)

- `POST /api/documents`
  - Ingests `CanonicalDocumentDto`, allocates sequence, builds XML, validates against local XSD, signs via XMLDSig, and transmits to DGII.
  - Returns `202 Accepted` with document ID, e-NCF, state, trackId, and security code.
- `GET /api/documents/by-source/{txnId}`
  - Retrieves persisted document status and metadata using the upstream ERP transaction ID.

### Direct DGII Operations (`/api/ecf`)

- `POST /api/ecf/send`
  - Sends a pre-signed e-CF XML directly to the DGII Asynchronous Reception endpoint.
- `POST /api/ecf/send-rfce`
  - Validates, serializes, signs, and posts an electronic consumer summary (`Rfce`) to DGII.
- `GET /api/ecf/status?rncEmisor={rnc}&eNcf={encf}`
  - Queries real-time fiscal acceptance status directly from the DGII consultation service.

### B2B Receiver Endpoints (`/fe/...`)

- `POST /fe/recepcion/api/ecf`
  - Ingests e-CF XML from supplier, verifies integrity, generates and signs an **ARECF** (Acuse de Recibo) with `Estado = 0`, and returns the signed ARECF XML.
- `POST /fe/aprobacioncomercial/api/ecf`
  - Ingests commercial approval XML (**ACECF**) from buyer.
- `GET /fe/autenticacion/api/semilla`
  - Generates an authentication seed for B2B buyer/seller mutual verification.
- `POST /fe/autenticacion/api/validacioncertificado`
  - Validates signed seed and returns a B2B session token.

### Authentication & Tenant Endpoints (`/api/auth`)

- `POST /api/auth/register`: Creates API administrative and worker users.
- `POST /api/auth/login`: Authenticates user credentials and issues JWT Bearer token.

---

## Solution Structure

```text
src/
├── EcfDgii.Client.Domain/              # Enterprise domain core
│   ├── Common/          # AuditableEntity base class
│   ├── Entities/        # User, Customer, EcfDocument, EcfSequence, Rfce schemas
│   ├── Interfaces/      # IEcfClient, IEcfXmlSerializer, IEcfXmlSigner, Repositories
│   └── Exceptions/      # EcfSigningException, EcfValidationException
├── EcfDgii.Client.Application/         # CQRS Handlers, DTOs, and Business logic
│   ├── Documents/       # CanonicalDocumentDto, normalization rules, and handlers
│   ├── Ecf/             # SendEcf, SendRfce, GetEcfStatus commands and queries
│   └── Auth/            # Login and Register commands
├── EcfDgii.Client.Infrastructure/      # Database, Security, Cryptography, and DGII HTTP Client
│   ├── Configuration/   # EcfEmisorOptions and client configuration
│   ├── Dgii/            # DgiiDirectTransport, EcfTokenManager, EcfEnvironmentConfig
│   ├── Persistence/     # ApplicationDbContext, sequence managers, and migrations
│   ├── Security/        # EcfXmlSigner, EcfSecurityUtils, PasswordHasher, TokenService
│   └── Serialization/   # EcfSchemaValidator, EcfXsdFileNameResolver, EcfDecimalXmlWriter
├── EcfDgii.Client.Shared/              # Cross-cutting Result monad and string helpers
├── EcfDgii.Client.Api/                 # ASP.NET Core REST API host and controllers
│   ├── Controllers/     # DocumentsController, EcfController, EmisorReceptorController, AuthController
│   └── Program.cs       # Middleware pipeline, DI registration, and startup verification
└── EcfDgii.Client.Tests/               # Automated test suite
    ├── UnitTests/       # Unit tests for XML builder, XSD validator, and signer
    └── IntegrationTests/# End-to-end API integration tests
```

---

## Installation & Setup

### Method 1: Local .NET CLI

1. Clone the repository:
   ```bash
   git clone https://github.com/JorgeGBeltre/EcfDgi.Client.git
   cd EcfDgi.Client
   ```
2. Build the solution:
   ```bash
   dotnet build EcfDgii.Client.slnx
   ```
3. Run the API host:
   ```bash
   dotnet run --project src/EcfDgii.Client.Api/EcfDgii.Client.Api.csproj
   ```

### Method 2: Docker Compose

1. Build and launch PostgreSQL, Redis, and the API container:
   ```bash
   docker compose up --build -d
   ```
2. Inspect operational logs:
   ```bash
   docker compose logs -f api
   ```

---

## Dependencies

```xml
<!-- Core Database & Persistence -->
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.2" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.9" />

<!-- CQRS Architecture & Validation -->
<PackageReference Include="MediatR" Version="12.4.1" />
<PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="11.11.0" />

<!-- Security, Authentication & Cryptography -->
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.9" />
<PackageReference Include="BCrypt.Net-Next" Version="4.0.3" />
<PackageReference Include="System.Security.Cryptography.Xml" Version="10.0.0-preview.2.25163.2" />

<!-- Observability & API Documentation -->
<PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
<PackageReference Include="Scalar.AspNetCore" Version="2.16.6" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.16.0" />
```

---

## Configuration & Options

Configure credentials and paths in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=ecf_dgii;Username=postgres;Password=postgres"
  },
  "JwtSettings": {
    "Secret": "ReplaceWithSecureKeyOfAtLeast32BytesLengthForProductionUseOnly!",
    "ExpirationMinutes": 60,
    "Issuer": "EcfDgiiClientIssuer",
    "Audience": "EcfDgiiClientAudience"
  },
  "EcfEmisor": {
    "Rnc": "101889063",
    "RazonSocial": "WILLY CHIC DOMINICANA SRL"
  },
  "EcfClientOptions": {
    "BaseUrl": "https://ecf.dgii.gov.do",
    "Environment": "Cert",
    "Mode": "DgiiDirect",
    "RncEmisor": "101889063",
    "CertificatePath": "C:/config/credentials/dgii_cert.p12",
    "CertificatePassword": "CertPassword123",
    "ValidateSchemasLocal": true,
    "XsdDirectoryPath": "Documentación Técnica (XSD)",
    "AutoRetryOnReuseableSequence": true
  }
}
```

---

## Database Persistence & Migrations

Entity Framework Core migrations manage the PostgreSQL database schema:

```bash
# Add a new migration
dotnet ef migrations add AddNewFiscalFields --project src/EcfDgii.Client.Infrastructure --startup-project src/EcfDgii.Client.Api

# Update database schema
dotnet ef database update --project src/EcfDgii.Client.Infrastructure --startup-project src/EcfDgii.Client.Api
```

---

## Docker Orchestration

The project includes a `Dockerfile` and `docker-compose.yml` for containerized deployments:

```bash
# Start all containers
docker compose up -d

# Stop and remove containers
docker compose down
```

---

## Diagnostics & Testing

The repository includes a comprehensive test suite covering unit tests, schema audits, concurrency tests, and end-to-end integration scenarios.

```bash
# Execute all unit and integration tests
dotnet test EcfDgii.Client.slnx
```

### Health Check Endpoint

Check service health by querying:

```bash
curl http://localhost:8080/health
```

Response:
```json
{
  "status": "Healthy"
}
```

### Scalar OpenAPI Console

When running in Development mode, the interactive Scalar API console is available at:
`http://localhost:8080/scalar/v1`

---

## License

Licensed under the **MIT License**. See [LICENSE](LICENSE) for details.

---

## Contact

Author: **Jorge Gaspar Beltre Rivera**  
Project: **EcfDgii.Client API & SDK**

<p align="center">
  <a href="https://www.linkedin.com/in/jorge-gaspar-beltre-rivera/" target="_blank"><img src="https://user-images.githubusercontent.com/74038190/235294012-0a55e343-37ad-4b0f-924f-c8431d9d2483.gif" alt="LinkedIn" width="100"></a>
  <a href="https://github.com/JorgeGBeltre" target="_blank"><img src="https://user-images.githubusercontent.com/74038190/212257468-1e9a91f1-b626-4baa-b15d-5c385dfa7ed2.gif" alt="GitHub" width="100"></a>
  <a href="mailto:Jorgegaspar3021@gmail.com"><img src="https://user-images.githubusercontent.com/74038190/216122065-2f028bae-25d6-4a3c-bc9f-175394ed5011.png" alt="E-Mail" width="100"></a>

</p>

## Support

This project is developed independently. Even a small contribution helps me dedicate more time to development, testing, and releasing new features.


 <p align="center">
  <a href="https://www.paypal.com/donate/?hosted_button_id=2VLA8BWT967LU">
    <img src="https://www.paypalobjects.com/webstatic/icon/pp258.png"
         alt="Donate with PayPal"
         height="60">
  </a>
</p>
