using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

public static class RoleAssignmentService
{
    const string ServiceBusDataOwnerRoleId = "090c5cfd-751d-490a-894a-3ce6f1109419";

    /// <summary>Assigns the signed-in user Service Bus Data Owner on the namespace. Returns false if already assigned.</summary>
    public static async Task<bool> GrantDataOwnerToSelfAsync(NamespaceRef ns)
    {
        var token = await ns.Credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), default);
        var objectId = Guid.Parse(JwtClaim(token.Token, "oid"));
        var roleDefinition = new ResourceIdentifier(
            $"/subscriptions/{ns.Resource.Id.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{ServiceBusDataOwnerRoleId}");
        try
        {
            await ns.Resource.GetRoleAssignments().CreateOrUpdateAsync(WaitUntil.Completed, Guid.NewGuid().ToString(),
                new RoleAssignmentCreateOrUpdateContent(roleDefinition, objectId) { PrincipalType = RoleManagementPrincipalType.User });
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    static string JwtClaim(string jwt, string claim)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty(claim).GetString()!;
    }
}
