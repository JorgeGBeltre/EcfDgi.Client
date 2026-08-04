namespace EcfDgii.Client.Api.Infrastructure.Security
{
    public class WorkerKeyInfo
    {
        public string KeyId { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public List<string> AllowedRncs { get; set; } = new();
        public DateTimeOffset? ValidUntil { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public interface IWorkerKeyResolver
    {
        Task<WorkerKeyInfo?> GetKeyInfoAsync(string keyId);
    }

    public class ConfigurationWorkerKeyResolver : IWorkerKeyResolver
    {
        private readonly IConfiguration _config;
        private readonly IHostEnvironment _env;

        public ConfigurationWorkerKeyResolver(IConfiguration config, IHostEnvironment env)
        {
            _config = config;
            _env = env;
        }

        public Task<WorkerKeyInfo?> GetKeyInfoAsync(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId))
                return Task.FromResult<WorkerKeyInfo?>(null);

            // Fast startup check: Reject default secret in non-development environments
            var configuredKeyId = _config["WORKER_KEY_ID"] ?? _config["WorkerKeyId"] ?? "default-worker-id";
            var configuredSecret = _config["WORKER_SECRET_KEY"] ?? _config["WorkerSecretKey"];

            if (!_env.IsDevelopment() && (configuredSecret == "WorkerSecretKey" || string.IsNullOrWhiteSpace(configuredSecret)))
            {
                throw new InvalidOperationException("Worker secret key is insecure or unconfigured for production environment.");
            }

            if (string.IsNullOrWhiteSpace(configuredSecret))
            {
                configuredSecret = "WorkerSecretKey";
            }

            if (string.Equals(keyId, configuredKeyId, StringComparison.OrdinalIgnoreCase))
            {
                var info = new WorkerKeyInfo
                {
                    KeyId = configuredKeyId,
                    Secret = configuredSecret,
                    TenantId = _config["WORKER_TENANT_ID"] ?? _config["WorkerTenantId"] ?? "default-tenant",
                    AllowedRncs = (_config["WORKER_ALLOWED_RNCS"] ?? _config["WorkerAllowedRncs"] ?? "*")
                                  .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .ToList(),
                    IsActive = true
                };
                return Task.FromResult<WorkerKeyInfo?>(info);
            }

            return Task.FromResult<WorkerKeyInfo?>(null);
        }
    }
}
