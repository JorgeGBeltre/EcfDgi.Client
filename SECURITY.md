# Security Policy

## Supported Versions
Security fixes are prioritized for the latest maintained version on `main` / `develop`. Support for older commits or unmaintained forks is not guaranteed; always upgrade to the latest maintained revision unless maintainers explicitly announce otherwise.

| Version | Security support |
|---|---|
| Latest maintained revision (`2.x` / `main`) | Security fixes prioritized |
| Legacy `1.x` SDK / older snapshots | Not guaranteed |

Repository release notes and commit history are the source of truth for current maintenance status.

## Reporting a Vulnerability
**Do not report security vulnerabilities through public issues, discussions, pull requests, or public comments.**

Use GitHub **Private Vulnerability Reporting** ([Create a private security advisory](https://github.com/JorgeGBeltre/EcfDgi.Client/security/advisories/new)) to report vulnerabilities confidentially to the maintainers. If private advisory submission is unavailable, contact the maintainer privately via the address listed in [README.md](README.md#contact) without sending unencrypted exploit payloads or third-party credentials over insecure channels.

Include, where safely possible:
- Vulnerability description and affected commit, version, project, and endpoint/component.
- Preconditions and required configuration (e.g., authentication policy, multi-tenant header setup).
- Reproduction steps or a minimal synthetic proof of concept.
- Expected security boundary versus actual impact.
- Relevant logs or HTTP traces with all secrets, certificates, and personal/taxpayer data redacted.
- Any known workaround or configuration mitigation.

Allow maintainers a reasonable opportunity to investigate, validate, and release a fix before any public disclosure. Do not access, modify, disrupt, or retain third-party data during research.

## Sensitive Data: Never Publish
Never attach, paste, or commit:
- Production PKCS#12 (`.p12` / `.pfx`) or PEM certificates and private keys.
- Certificate passwords or master encryption secrets (`STORAGE_ENCRYPTION_KEY`, `JWT_SECRET`, `JwtSettings:Secret`, `WORKER_SECRET_KEY`).
- DGII authentication seeds, Bearer tokens, API keys, or session credentials.
- Production `.env` files, connection strings, Redis credentials, or PostgreSQL database dumps.
- Real signed `<ECF>`, `<RFCE>`, `<ARECF>`, `<ACECF>`, or `<ANECF>` XML documents containing identifiable taxpayer, customer, or employee data.

Always use generated ephemeral test certificates, synthetic RNCs/identifiers (such as `101000001`), and redacted logs.

## Security-Critical Areas
Reports are especially relevant to:
- X.509 / PKCS#12 certificate loading, password handling, RNC subject/OU isolation, bounded signer caching, and key lifecycle (`EcfXmlSigner`, `TenantSignerResolver`).
- Symmetric key derivation and authenticated encryption, including HKDF-SHA256 and AES-GCM (`enc:v1:` / `enc:v2:` / `enc:v3:`) certificate secret decryption.
- W3C XMLDSig RSA-SHA256 (`EnvelopedSignature` + inclusive C14N) signing, verification, and `CodigoSeguridad` derivation.
- XML parsing, canonicalization, and 15-schema XSD validation (`EcfSchemaValidator`), including `DtdProcessing.Prohibit` and `XmlResolver = null` protections against XXE and external entity resolution.
- DGII `Semilla` challenge authentication, distributed Redis/memory token caching (`EcfTokenManager`), and HTTPS/TLS certificate validation (`DgiiDirectTransport`, `EcfClient`).
- `JwtBearer` authentication, BCrypt password hashing, and `WorkerAuth` HMAC-SHA256 request canonicalization, timestamp window validation, and anti-replay nonce enforcement (`WorkerAuthenticationHandler`).
- Multi-tenant (`X-Tenant-Id`) and multi-environment (`X-Environment`) isolation across PostgreSQL records, sequence allocation, idempotency keys (`IdempotencyMiddleware`), and per-tenant rate limiting.
- Cryptographic and schema validation on `[AllowAnonymous]` B2B Emisor-Receptor interoperability endpoints (`/fe/...` in `EmisorReceptorController`).
- Sensitive data exposure through Serilog/OpenTelemetry logs, RFC 9457 `ProblemDetails` error responses, or generated PDFs.
- NuGet dependency vulnerabilities and untrusted input handling.

This list is not exhaustive. Report actual observed behavior and affected revisions.

## What to Expect
Maintainers will make reasonable efforts to acknowledge reports privately, evaluate severity and reproducibility, coordinate investigation, prepare and test a fix or mitigation, and publish a security advisory and patched revision when appropriate. Timelines depend on severity, complexity, upstream dependencies, and maintainer availability; this policy does not establish a fixed SLA.

## Coordinated Disclosure
Do not publicly disclose an unpatched vulnerability or working exploit details prior to coordinated remediation with maintainers. Once a fix or mitigation is available, maintainers may publish a GitHub Security Advisory detailing impact, affected revisions, and upgrade guidance.

## Scope and Good-Faith Research
This policy covers the `JorgeGBeltre/EcfDgi.Client` repository and its maintained artifacts. Vulnerabilities in DGII government infrastructure, `.NET`, `PostgreSQL`, `Redis`, Docker base images, or third-party NuGet packages should be reported to their respective upstream maintainers; privately share any `EcfDgii.Client`-specific impact with this project's maintainers.

All security testing must be authorized, non-destructive, and strictly limited to local/isolated environments and synthetic data you own or are explicitly permitted to test.
