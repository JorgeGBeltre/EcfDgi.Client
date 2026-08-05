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
    }
}
