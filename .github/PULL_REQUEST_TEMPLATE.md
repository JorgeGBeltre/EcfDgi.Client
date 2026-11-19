## Summary
<!-- Explain what this pull request changes and why. -->

## Related issue
<!-- Fixes #123, Closes #123, or References #123. -->

## Change type
- [ ] Bug fix
- [ ] New feature
- [ ] Fiscal/compliance correction (DGII e-CF / XSD)
- [ ] Security improvement
- [ ] Refactoring
- [ ] Documentation
- [ ] Tests, CI/CD, or tooling
- [ ] Breaking change

## Affected components
- [ ] `EcfDgii.Client.Domain` (entities, interfaces, domain exceptions)
- [ ] `EcfDgii.Client.Application` (CQRS handlers, `CanonicalDocumentDto`, validators)
- [ ] `EcfDgii.Client.Infrastructure` (PostgreSQL/EF Core, DGII transport, reconciler)
- [ ] `EcfDgii.Client.Api` (controllers, middleware, `/fe/...` B2B endpoints)
- [ ] `EcfDgii.Client.Shared` or `EcfDgii.TestRunner`
- [ ] XML generation or 15-schema XSD validation
- [ ] W3C XMLDSig signing, `TenantSignerResolver`, or `CodigoSeguridad`
- [ ] Multi-currency (`OtraMoneda`) or monetary calculations
- [ ] Security, `JwtBearer` / `WorkerAuth` HMAC, or rate limiting
- [ ] Docker, CI/CD, or configuration
- [ ] Documentation
- [ ] Other: <!-- specify -->

## Technical description
<!-- Describe the implementation, affected Clean Architecture layers, and key design decisions. -->

## Fiscal impact
<!-- Identify affected e-CF types (E31-E47, RFCE, ARECF, ACECF, ANECF), XML sections/tags,
DGII validation rules, XSD schemas, decimal rounding, or QR Timbre URLs.
If not applicable, write "Not applicable". -->

## Security impact
<!-- Explain changes to PKCS#12 certificates, HKDF/AES-GCM decryption, XMLDSig signing,
XML parser settings (XXE/DTD), JwtBearer / WorkerAuth HMAC, multi-tenant isolation, or logging.
If not applicable, write "Not applicable". -->

## Persistence, multi-tenancy, and API impact
<!-- Describe EF Core models/migrations, PostgreSQL indexes or sequence allocation,
Redis caching, X-Tenant-Id / X-Environment isolation, IdempotencyMiddleware, or REST contracts.
If not applicable, write "Not applicable". -->

## Testing
Commands executed and results:

```text
# Paste exact dotnet / docker commands executed and their output summary
```

- [ ] Regression tests added or updated (`UnitTests` / `IntegrationTests`).
- [ ] Official XSD validation tests (`EcfDocumentXsdValidationTests`) added or updated where XML changed.
- [ ] Full solution test suite (`dotnet test EcfDgii.Client.slnx -c Release`) passes.
- [ ] Failure, concurrency, and boundary cases considered.
- [ ] NuGet vulnerability check (`dotnet list EcfDgii.Client.slnx package --vulnerable --include-transitive`) reviewed.
- [ ] Tests were not run (explain why below).

Testing notes:
<!-- Include test counts, limitations, and environment details. -->

## Compatibility and migration
- [ ] No breaking changes.
- [ ] Breaking changes documented with migration guidance.
- [ ] Public REST API or `CanonicalDocumentDto` changes documented.
- [ ] EF Core migrations, PostgreSQL schema, or configuration changes documented.
- [ ] `hmac_test_vectors.json` compatibility preserved.

## Documentation
- [ ] `README.md` updated incrementally where necessary.
- [ ] Examples and fixtures use strictly synthetic data (no real RNCs, names, or secrets).
- [ ] No documentation update required.

## Pull Request Checklist

### Architecture
- [ ] Clean Architecture boundaries (`Domain` <- `Application` <- `Infrastructure` / `Api`) are respected.
- [ ] DGII fiscal and XML rules remain encapsulated inside the appropriate `EcfDgii.Client` components.
- [ ] Changes are focused, minimal, and avoid unrelated refactoring or inline comment noise in `src/`.
- [ ] No unnecessary NuGet dependencies introduced.

### Fiscal correctness
- [ ] Applicable DGII technical documentation, validation rules, and XSD schemas reviewed when relevant.
- [ ] C# `decimal` precision, `MidpointRounding.AwayFromZero`, and line/bucket cuadratura preserved.
- [ ] Foreign-currency (`OtraMoneda`) DOP conversion and `RD$250,000.00` RFCE thresholds verified when relevant.
- [ ] Existing e-CF types and pre-allocation sequence guards considered.

### Security
- [ ] No credentials, `.p12`/`.pfx` certificates, private keys, tokens, or real taxpayer data committed.
- [ ] Logs and `ProblemDetails` responses do not leak sensitive information.
- [ ] Authentication (`JwtBearer` / `WorkerAuth`), authorization, and input validation preserved.
- [ ] XML parser protections (`DtdProcessing.Prohibit`, `XmlResolver = null`) and XMLDSig verification were not weakened.

### Multi-tenancy & Persistence
- [ ] Tenant (`X-Tenant-Id`) and environment (`X-Environment`) isolation preserved.
- [ ] Sequence concurrency, idempotency, and `Uncertain` verbatim retransmission invariants preserved.

### Final review
- [ ] I reviewed my own `git diff`.
- [ ] Temporary, build, and unrelated files were excluded.
- [ ] I followed [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) and [CONTRIBUTING.md](../CONTRIBUTING.md).
