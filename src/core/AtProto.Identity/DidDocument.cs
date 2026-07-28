using System.Text.Json;
using AtProto.Lexicon;

namespace AtProto.Identity;

/// <summary>
/// The resolved essentials of a DID document: the account handle, its PDS service endpoint,
/// and the atproto signing key (Multikey, <c>z...</c> multibase).
/// </summary>
public sealed record DidDocument(
    string Did,
    string? Handle,
    string? PdsEndpoint,
    string? SigningKeyMultibase)
{
    private const string AtprotoPdsType = "AtprotoPersonalDataServer";

    /// <summary>Parse a W3C DID document (as served by plc.directory or /.well-known/did.json).</summary>
    public static DidDocument Parse(JsonElement doc)
    {
        string did = doc.GetProperty("id").GetString()
            ?? throw new FormatException("DID document missing 'id'");

        string? handle = null;
        if (doc.TryGetProperty("alsoKnownAs", out JsonElement aka) && aka.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement alias in aka.EnumerateArray())
            {
                if (alias.GetString() is string s && AtUri.TryParse(s, out AtUri uri))
                {
                    handle = uri.Authority;
                    break;
                }
            }
        }

        string? pds = null;
        if (doc.TryGetProperty("service", out JsonElement services) && services.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement svc in services.EnumerateArray())
            {
                if (svc.TryGetProperty("type", out JsonElement type)
                    && type.GetString() == AtprotoPdsType
                    && svc.TryGetProperty("serviceEndpoint", out JsonElement ep))
                {
                    pds = ep.GetString();
                    break;
                }
            }
        }

        string? key = null;
        if (doc.TryGetProperty("verificationMethod", out JsonElement vms) && vms.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement vm in vms.EnumerateArray())
            {
                if (vm.TryGetProperty("id", out JsonElement id)
                    && id.GetString() is string idStr
                    && idStr.EndsWith("#atproto", StringComparison.Ordinal)
                    && vm.TryGetProperty("publicKeyMultibase", out JsonElement pk))
                {
                    key = pk.GetString();
                    break;
                }
            }
        }

        return new DidDocument(did, handle, pds, key);
    }

    public static DidDocument Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }
}
