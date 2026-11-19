# Contributing to EcfDgii.Client

Thank you for considering a contribution. **EcfDgii.Client** (`EcfDgi.Client`) is a `.NET 10` SDK and multi-tenant ASP.NET Core REST API built on Clean Architecture for Dominican Republic electronic invoicing (**DGII e-CF**). Contributions must preserve fiscal correctness, security, backwards compatibility, and strict separation across architectural layers.

Please read the [Code of Conduct](CODE_OF_CONDUCT.md). For security vulnerabilities, do not open a public issue; follow [SECURITY.md](SECURITY.md).

## 1. Ways to Contribute
You can report reproducible defects, propose enhancements, improve documentation, add unit or XSD/integration tests, fix defects, or review pull requests.

## 2. Before Contributing
1. Search existing issues and pull requests.
2. Check current documentation ([README.md](README.md)) and implemented behavior.
3. Discuss substantial architectural or API contract changes before implementation.
4. Describe the problem and expected behavior with concrete evidence.
5. Never include production credentials, `.p12`/`.pfx` certificates, private keys, encryption secrets, JWT/HMAC tokens, or identifiable taxpayer data.

## 3. Architecture and Boundaries
The solution (`EcfDgii.Client.slnx`) follows Clean Architecture across distinct projects under `src/`:
- **`EcfDgii.Client.Domain`**: Core entities (`EcfDocument`, `EcfSequence`, `Customer`, `User`, `Rfce`), `AuditableEntity`, domain exceptions, and abstractions (`IEcfClient`, `IEcfXmlSigner`, `ITenantSignerResolver`, `IEcfSequenceManager`). Must remain free of Entity Framework Core, HTTP transport, or ASP.NET Core dependencies.
- **`EcfDgii.Client.Application`**: MediatR CQRS commands and queries, canonical contracts (`CanonicalDocumentDto`), FluentValidation rules (`CanonicalDocumentDtoValidator`), and domain fiscal validators (`EcfValidator`).
- **`EcfDgii.Client.Infrastructure`**: PostgreSQL persistence (`ApplicationDbContext`, atomic `EcfSequenceManager`, EF Core migrations), cryptography (`EcfXmlSigner`, `TenantSignerResolver`), local 15-schema XSD validation (`EcfSchemaValidator`), DGII HTTP/TLS transport (`DgiiDirectTransport`, `EcfTokenManager`, `EcfClient`), and background status reconciliation (`EcfStatusReconciler`, `EcfStatusPollingBackgroundService`).
- **`EcfDgii.Client.Shared`**: Cross-cutting `Result<T>` primitives and legacy Serie B / e-NCF normalization (`NcfNormalizer`).
- **`EcfDgii.Client.Api`**: ASP.NET Core 10 REST host, controllers (`/api/documents`, `/api/ecf`, `/fe/...` B2B interoperability, `/api/customers`, `/api/auth`), authentication (`JwtBearer` and `WorkerAuthenticationHandler` HMAC-SHA256), `IdempotencyMiddleware`, per-tenant rate limiting, and RFC 9457 `ProblemDetails`.
- **`EcfDgii.TestRunner` & `EcfDgii.Client.Tests`**: Offline sample receipt/simulation runner (`--sample-pdfs`) and automated `UnitTests` and `IntegrationTests`.

Rules:
- Respect inward dependency flow (`Domain` <- `Application` <- `Infrastructure` / `Api`).
- Keep all DGII fiscal rules, XML tag ordering, `<OtraMoneda>` conversion, and XSD compliance inside `EcfDgii.Client` rather than pushing fiscal responsibilities to upstream callers.
- Reuse existing normalizers, validators, signers, and transport abstractions instead of duplicating fiscal logic.
- Avoid circular dependencies and speculative abstractions.
- Justify any new NuGet package dependency and verify it introduces no known vulnerabilities.
- Preserve public REST endpoints, `CanonicalDocumentDto` fields, and pinned `hmac_test_vectors.json` vectors unless a breaking change is explicitly justified and documented.

Follow the existing solution layout; do not create parallel project structures without a clear reason.

## 4. Development Environment
Use the `.NET 10 SDK` (`10.0.x`) declared by the solution and CI pipeline (`.github/workflows/ci.yml`). Restore and build through `EcfDgii.Client.slnx`. For full containerized runtime verification with PostgreSQL and Redis, use `docker compose up --build -d`. Do not assume an undocumented local tool or SDK preview is available in CI.

## 5. Coding Standards
- Follow established C# and .NET conventions in the touched project (nullable reference types enabled, explicit naming, async/await with `CancellationToken` propagation).
- Keep C# source files under `src/` clean and self-documenting, consistent with the repository's comment-free `src/` style.
- Handle errors explicitly using `Result<T>`, domain exceptions, or RFC 9457 `ProblemDetails`; do not silently swallow exceptions.
- Never log secrets, raw PKCS#12 bytes, certificate passwords, master keys, tokens, or unredacted personal/fiscal payloads.
- Avoid unrelated formatting diffs or mass reformatting.
- Do not commit `bin/`, `obj/`, `publish/`, `.vs/`, `.env`, `.p12`/`.pfx` files, local databases, or test coverage artifacts.

## 6. Fiscal and Monetary Correctness
Changes involving e-CF types (`E31`–`E34`, `E41`, `E43`–`E47`), `RFCE 32`, `ARECF`, `ACECF`, `ANECF`, `Semilla`, XML fields, DGII validation rules (such as `[613]`, `[615]`, `[1381]`), ITBIS/ISR retentions, discounts, credit/debit notes, or QR `Timbre` URLs require particular care:
- Identify the affected `TipoeCF` / schema and exact XML section (`<IdDoc>`, `<Emisor>`, `<Comprador>`, `<Totales>`, `<OtraMoneda>`, `<DetallesItems>`, `<InformacionReferencia>`).
- Reference the applicable official DGII technical specification, validation rule, or XSD in `Documentación Técnica (XSD)`.
- Distinguish official DGII requirements from ERP-normalization conveniences.
- Add valid, invalid, and boundary tests (including pre-sequence-allocation rejection checks where applicable).
- Preserve unaffected document types and workflows.
- Use strictly synthetic demo RNCs, names, and values (e.g., `101000001`, `EMPRESA DEMO SRL`).

### Decimal and currency handling
- Use C# `decimal` (`System.Decimal`) exclusively for monetary amounts, quantities, unit prices, discounts, retentions, tax bases, and exchange rates; never use `float` or `double`.
- Follow existing `MidpointRounding.AwayFromZero` and decimal formatting conventions (`EcfDecimalXmlWriter`).
- Preserve cent-level internal cuadratura (`CantidadItem * PrecioUnitarioItem - DescuentoMonto == MontoItem` and consistent DOP bucket totals).
- When modifying foreign-currency (`OtraMoneda` / `OtraMonedaDetalle`) logic, test conversion into `DOP` across the 17 official DGII `TipoMonedaType` ISO codes, including `E32` `RD$250,000.00` RFCE threshold evaluation in `DOP`.

### XML and signatures
- Validate generated XML against the corresponding official XSD in `Documentación Técnica (XSD)` via `EcfSchemaValidator`.
- Preserve exact XSD element ordering and omit empty tags where prohibited by DGII schemas.
- Preserve W3C XMLDSig RSA-SHA256 (`http://www.w3.org/2001/04/xmldsig-more#rsa-sha256`), inclusive C14N (`http://www.w3.org/TR/2001/REC-xml-c14n-20010315`), and 6-character Base64 `<SignatureValue>` `CodigoSeguridad` semantics.
- Treat external XML (including `/fe/...` B2B payloads and DGII responses) as untrusted input: keep `XmlResolver = null` and `DtdProcessing = DtdProcessing.Prohibit`.
- Do not weaken XSD validation or signature verification to make a test pass.
- Use generated ephemeral test certificates in tests; never commit real certificates or private keys.

Do not claim DGII homologation or production certification outcomes without verifiable evidence.

## 7. Multi-Tenancy, Persistence, and API Reliability
- Preserve strict tenant (`X-Tenant-Id`) and environment (`X-Environment`: `PreCertificacion`, `Certificacion`, `Produccion`) isolation across sequence allocation, document lookups, certificate resolution, and background polling.
- Maintain composite uniqueness invariants (`(tenant_id, ambiente, source_txn_id)` and `(rnc_emisor, ambiente, e_ncf)`) and `IdempotencyMiddleware` guarantees.
- Use parameterized Entity Framework Core operations; generate explicit EF Core migrations when persistent models change:
  ```bash
  dotnet ef migrations add <MigrationName> --project src/EcfDgii.Client.Infrastructure --startup-project src/EcfDgii.Client.Api
  ```
- Preserve `UserOrWorker` / `WorkerOnly` authorization policies on `/api/...` routes and cryptographic verification on `[AllowAnonymous]` `/fe/...` B2B routes.
- When modifying `Uncertain` reconciliation or retransmission (`EcfStatusReconciler`), ensure persisted `SignedXmlContent` / `SignedRfceContent` is reused verbatim so `<CodigoSeguridadeCF>` and printed QR codes remain immutable.

## 8. Tests and Quality Checks
Run targeted tests for the affected area and the full solution test suite before opening a pull request:

```bash
dotnet restore EcfDgii.Client.slnx
dotnet list EcfDgii.Client.slnx package --vulnerable --include-transitive
dotnet build EcfDgii.Client.slnx -c Release --no-restore
dotnet test EcfDgii.Client.slnx -c Release --no-build --verbosity normal
```

Add regression tests for bug fixes, XSD validation tests (`EcfDocumentXsdValidationTests`) when XML output changes, and boundary/failure tests for fiscal or security-sensitive logic. List the exact commands executed and their results in your pull request. Never claim tests passed if they were not executed.

## 9. Pull Request Process
1. Create a focused branch from `develop`.
2. Make the smallest complete change that solves the problem cleanly.
3. Add or update unit, XSD, or integration tests and relevant documentation.
4. Review `git status` and `git diff` for secrets, real taxpayer data, generated artifacts, unrelated edits, or unintended contract changes.
5. Open a pull request targeting `develop` using the repository pull request template.
6. Address review feedback with clear technical explanations and focused follow-up commits.

## 10. Commit Messages
Write clear English commit messages following [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```text
type(scope): concise description
```

Examples: `fix(documents): enforce DGII rule 1381 RNCComprador precedence on E34 notes`, `feat(otra-moneda): support DOP conversion for foreign-currency e-CFs`, `test(security): verify TenantSignerResolver AES-GCM envelope decryption`.

Do not add co-author trailers or attribution for people or tools that did not author the commit.

## 11. Breaking Changes and Releases
Do not introduce breaking changes silently. Explain affected REST endpoints, `CanonicalDocumentDto` properties, database schema/migrations, configuration keys, and worker authentication headers; provide migration guidance; update tests and documentation; and highlight the change in the pull request. Release and deployment decisions are managed by maintainers.

## 12. Documentation
Update [README.md](README.md) incrementally when supported configurations, REST endpoints, `CanonicalDocumentDto` fields, e-CF behaviors, currency/rounding rules, or security controls change. Use synthetic examples exclusively.

## 13. Questions
Open an issue with sufficient technical context using the appropriate template. For security vulnerabilities, follow [SECURITY.md](SECURITY.md).
