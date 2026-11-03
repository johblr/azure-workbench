using Azure.Core;
using Azure.Identity;

namespace AzureWorkbench.Services;

public enum AuthMode { AzureCli, InteractiveBrowser }

public class CredentialProvider(string? stateDirectory = null, Func<bool>? allowUnencryptedCache = null)
{
    readonly Dictionary<string, TokenCredential> _cache = new();

    public AuthMode Mode { get; private set; }

    /// <summary>Raised (on the calling thread) when the OS token store is unavailable and sign-in falls back to memory only.</summary>
    public event Action<string>? Notice;

    public void Reset(AuthMode mode)
    {
        Mode = mode;
        _cache.Clear();
    }

    public TokenCredential For(string? tenantId)
    {
        var key = tenantId ?? "";
        if (_cache.TryGetValue(key, out var cred)) return cred;
        cred = Mode == AuthMode.AzureCli
            ? new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId })
            : stateDirectory == null
                ? new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions { TenantId = tenantId })
                : new RememberedBrowserCredential(tenantId, stateDirectory, allowUnencryptedCache ?? (() => false), message => Notice?.Invoke(message));
        return _cache[key] = cred;
    }

    /// <summary>Deletes remembered browser sign-in records. Cached tokens in the OS store expire on their own.</summary>
    public void ForgetBrowserSignIns()
    {
        _cache.Clear();
        if (stateDirectory == null || !Directory.Exists(stateDirectory)) return;
        foreach (var file in Directory.GetFiles(stateDirectory, "auth-*.json")) File.Delete(file);
    }

    /// <summary>
    /// Browser sign-in that survives restarts: tokens live in the OS credential store (Windows DPAPI, macOS Keychain,
    /// Linux libsecret) and a non-secret AuthenticationRecord identifies the account. If the OS store is unavailable
    /// (e.g. no keyring on WSL), it falls back to an in-memory cache for this run.
    /// </summary>
    sealed class RememberedBrowserCredential : TokenCredential
    {
        readonly string? _tenantId;
        readonly string _recordPath;
        readonly Action<string> _notice;
        readonly Func<bool> _allowUnencrypted;
        readonly SemaphoreSlim _gate = new(1, 1);
        InteractiveBrowserCredential _inner;
        AuthenticationRecord? _record;
        bool _persistent = true;
        bool _unencrypted;

        public RememberedBrowserCredential(string? tenantId, string directory, Func<bool> allowUnencrypted, Action<string> notice)
        {
            _tenantId = tenantId;
            _notice = notice;
            _allowUnencrypted = allowUnencrypted;
            _recordPath = Path.Combine(directory, $"auth-{(string.IsNullOrEmpty(tenantId) ? "default" : tenantId)}.json");
            try
            {
                if (File.Exists(_recordPath))
                {
                    using var stream = File.OpenRead(_recordPath);
                    _record = AuthenticationRecord.Deserialize(stream);
                }
            }
            catch (Exception) { _record = null; }
            _inner = Create();
        }

        InteractiveBrowserCredential Create() => new(new InteractiveBrowserCredentialOptions
        {
            TenantId = _tenantId,
            AuthenticationRecord = _persistent ? _record : null,
            TokenCachePersistenceOptions = _persistent
                ? new TokenCachePersistenceOptions { Name = "AzureWorkbench", UnsafeAllowUnencryptedStorage = _unencrypted }
                : null,
        });

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                try { return await GetCoreAsync(requestContext, cancellationToken); }
                catch (Exception ex) when (_persistent && !_unencrypted && IsPersistenceFailure(ex))
                {
                    if (_allowUnencrypted())
                    {
                        _unencrypted = true;
                        _inner = Create();
                        _notice("No OS keyring is available; browser tokens are kept in an unencrypted file in your profile (as you allowed).");
                    }
                    else
                    {
                        _persistent = false;
                        _inner = Create();
                        _notice("The OS credential store is unavailable, so browser sign-in lasts until the app closes. " +
                            "Install a keyring (e.g. gnome-keyring), or use the command 'Remember browser sign-in without a keyring'.");
                    }
                    return await GetCoreAsync(requestContext, cancellationToken);
                }
            }
            finally { _gate.Release(); }
        }

        async Task<AccessToken> GetCoreAsync(TokenRequestContext context, CancellationToken ct)
        {
            if (_persistent && _record == null)
            {
                _record = await _inner.AuthenticateAsync(context, ct);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
                    await using var stream = File.Create(_recordPath);
                    await _record.SerializeAsync(stream, ct);
                }
                catch (IOException) { /* remembering is best effort */ }
            }
            return await _inner.GetTokenAsync(context, ct);
        }

        static bool IsPersistenceFailure(Exception ex) =>
            ex.GetType().Name.Contains("Msal", StringComparison.Ordinal) && ex.Message.Contains("persist", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Persistence check failed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("libsecret", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("keyring", StringComparison.OrdinalIgnoreCase)
            || ex.InnerException is { } inner && IsPersistenceFailure(inner);
    }
}
