using Azure.Core;
using Azure.Identity;

namespace AzureWorkbench.Services;

/// <summary>
/// Caches tokens from a credential that doesn't cache them itself (Azure CLI starts an <c>az</c> process per request).
/// Concurrent requests for the same scope share one in-flight fetch, tokens are reused until five minutes before
/// they expire, and an optional gate limits how many fetches run at once across all tenants.
/// </summary>
public sealed class CachingCredential(TokenCredential inner, SemaphoreSlim? gate = null, string? tenantId = null) : TokenCredential
{
    static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    readonly object _lock = new();
    readonly Dictionary<string, Task<AccessToken>> _tokens = new(StringComparer.Ordinal);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        // Claims challenges (e.g. conditional access) must always go to the real credential.
        if (!string.IsNullOrEmpty(requestContext.Claims)) return await FetchAsync(requestContext, cancellationToken);
        var key = string.Join(' ', requestContext.Scopes) + "|" + requestContext.TenantId;
        Task<AccessToken> task;
        lock (_lock)
        {
            if (!_tokens.TryGetValue(key, out task!) || task.IsFaulted || task.IsCanceled
                || task.IsCompletedSuccessfully && task.Result.ExpiresOn - DateTimeOffset.UtcNow < RefreshMargin)
                _tokens[key] = task = FetchAsync(requestContext, CancellationToken.None); // shared fetch: one caller cancelling must not fail the others
        }
        return await task.WaitAsync(cancellationToken);
    }

    async Task<AccessToken> FetchAsync(TokenRequestContext context, CancellationToken ct)
    {
        if (gate != null) await gate.WaitAsync(ct);
        try { return await inner.GetTokenAsync(context, ct); }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            // The CLI only holds tokens for tenants you've signed in to (some need their own sign-in for MFA).
            throw new AuthenticationFailedException(tenantId == null
                ? "Azure CLI isn't signed in. Run: az login  — then sign in again here."
                : $"Azure CLI can't get a token for tenant {tenantId}. Run: az login --tenant {tenantId}  — then right-click the tenant and Refresh.", ex);
        }
        finally { gate?.Release(); }
    }
}
