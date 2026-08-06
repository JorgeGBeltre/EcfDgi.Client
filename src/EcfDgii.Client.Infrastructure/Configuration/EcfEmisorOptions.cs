namespace EcfDgii.Client.Infrastructure.Configuration
{
    /// <summary>
    /// The RNC this API instance issues e-CF documents under.
    ///
    /// Deliberately instance-level config, not per-tenant, even though EcfDocument carries a
    /// TenantId (see uq_ecf_documents_tenant_source_txn): today this system has exactly one
    /// registered emisor per deployment, and TenantId exists for future multi-tenancy rather than
    /// per-tenant emisor resolution today. If this ever becomes genuinely multi-emisor, this value
    /// moves onto a Tenant entity and the failure mode becomes "tenant has no RNC configured" at
    /// tenant-resolution time — not a missing appsettings value at startup. Revisit this comment
    /// (and DocumentsController's use of it) before onboarding a second emisor.
    /// </summary>
    public class EcfEmisorOptions
    {
        public const string SectionName = "EcfEmisor";

        public string Rnc { get; set; } = string.Empty;

        /// <summary>
        /// The legal name (razón social) this instance signs e-CF documents under. Same reasoning as
        /// Rnc above: it's an instance-level fact of who this API IS, not something a caller (a
        /// connector adapter, or any other future integration) should be able to assert. Before this
        /// field existed, DocumentsController trusted the incoming DTO's RazonSocialEmisor verbatim —
        /// the RNC override didn't have a matching RazonSocial override, so a wrong or malicious
        /// caller-supplied name could land in a validly-signed e-CF under this instance's own
        /// enforced RNC.
        /// </summary>
        public string RazonSocial { get; set; } = string.Empty;
    }
}
