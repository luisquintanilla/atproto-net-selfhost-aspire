using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace AtProto.Pds;

/// <summary>The repository mutations that an OAuth permission can authorize.</summary>
public enum PermissionAction
{
    Create,
    Update,
    Delete,
}

/// <summary>A normalized grant for one repository collection.</summary>
public sealed record PermissionGrant
{
    public PermissionGrant(string collection, IEnumerable<PermissionAction> actions)
    {
        if (string.IsNullOrWhiteSpace(collection))
            throw new ArgumentException("A permission collection is required.", nameof(collection));
        ArgumentNullException.ThrowIfNull(actions);

        Collection = collection;
        Actions = new HashSet<PermissionAction>(actions);
        if (Actions.Count == 0)
            throw new ArgumentException("A permission grant must contain at least one action.", nameof(actions));
    }

    public string Collection { get; }
    public IReadOnlySet<PermissionAction> Actions { get; }

    public bool Allows(string collection, PermissionAction action) =>
        (Collection == "*" || string.Equals(Collection, collection, StringComparison.Ordinal))
        && Actions.Contains(action);
}

/// <summary>
/// The resolved, immutable permission state carried by an OAuth authorization. The transitional
/// scope is deliberately represented as a separate compatibility bit rather than as an implicit
/// wildcard, so new callers must opt into it explicitly.
/// </summary>
public sealed record PermissionSnapshot
{
    public PermissionSnapshot(bool allowsTransitionGeneric, IEnumerable<PermissionGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        AllowsTransitionGeneric = allowsTransitionGeneric;
        Grants = new ReadOnlyCollection<PermissionGrant>(Merge(grants));
    }

    public bool AllowsTransitionGeneric { get; }
    public IReadOnlyList<PermissionGrant> Grants { get; }

    public static PermissionSnapshot Empty { get; } = new(false, Array.Empty<PermissionGrant>());

    public bool Allows(string collection, PermissionAction action) =>
        AllowsTransitionGeneric || Grants.Any(grant => grant.Allows(collection, action));

    public string ToAccessTokenScope()
    {
        var scopes = new List<string> { "atproto" };
        if (AllowsTransitionGeneric)
            scopes.Add("transition:generic");

        scopes.AddRange(Grants
            .OrderBy(grant => grant.Collection, StringComparer.Ordinal)
            .Select(ToScope));
        return string.Join(' ', scopes);
    }

    private static string ToScope(PermissionGrant grant)
    {
        var actions = new[] { PermissionAction.Create, PermissionAction.Update, PermissionAction.Delete }
            .Where(grant.Actions.Contains)
            .Select(ActionName)
            .ToArray();
        if (actions.Length == 3)
            return $"repo:{grant.Collection}";

        return $"repo:{grant.Collection}?{string.Join('&', actions.Select(action => $"action={action}"))}";
    }

    private static List<PermissionGrant> Merge(IEnumerable<PermissionGrant> grants)
    {
        var merged = new Dictionary<string, HashSet<PermissionAction>>(StringComparer.Ordinal);
        foreach (PermissionGrant grant in grants)
        {
            if (!merged.TryGetValue(grant.Collection, out HashSet<PermissionAction>? actions))
            {
                actions = new HashSet<PermissionAction>();
                merged.Add(grant.Collection, actions);
            }
            actions.UnionWith(grant.Actions);
        }

        return merged
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new PermissionGrant(entry.Key, entry.Value))
            .ToList();
    }

    internal static string ActionName(PermissionAction action) => action switch
    {
        PermissionAction.Create => "create",
        PermissionAction.Update => "update",
        PermissionAction.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    internal static bool TryParseAction(string value, out PermissionAction action)
    {
        action = value switch
        {
            "create" => PermissionAction.Create,
            "update" => PermissionAction.Update,
            "delete" => PermissionAction.Delete,
            _ => default,
        };
        return value is "create" or "update" or "delete";
    }
}

/// <summary>A permission declaration as represented by a permission-set Lexicon.</summary>
public sealed record PermissionDeclaration(
    string Resource,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters)
{
    public static PermissionDeclaration Repo(string collection, params PermissionAction[] actions)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        var parameters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["collection"] = new[] { collection },
        };
        if (actions.Length > 0)
            parameters["action"] = actions.Select(PermissionSnapshot.ActionName).ToArray();
        return new PermissionDeclaration("repo", parameters);
    }
}

/// <summary>The input needed to resolve an <c>include:</c> scope.</summary>
public sealed record PermissionSetRequest(
    string Nsid,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters);

/// <summary>
/// A permission-set document returned by the permission resolver. Freshness is checked before a
/// new authorization is accepted; the resulting snapshot then remains fixed for that OAuth session.
/// </summary>
public sealed record PermissionSetResolution(
    string Nsid,
    IReadOnlyList<PermissionDeclaration> Permissions,
    DateTimeOffset? StaleAt = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Resolves public permission-set Lexicons. An implementation can adapt the pinned atproto-dotnet
/// permission/Lexicon resolver once that API is available without changing the PDS endpoints.
/// </summary>
public interface IPermissionSetResolver
{
    ValueTask<PermissionSetResolution?> ResolveAsync(
        PermissionSetRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Default resolver used until an application supplies a permission-set implementation.</summary>
public sealed class UnavailablePermissionSetResolver : IPermissionSetResolver
{
    public ValueTask<PermissionSetResolution?> ResolveAsync(
        PermissionSetRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PermissionSetResolution?>(null);
}

/// <summary>A small process-local resolver useful for hosts and tests that register known sets.</summary>
public sealed class InMemoryPermissionSetResolver : IPermissionSetResolver
{
    private readonly IReadOnlyDictionary<string, PermissionSetResolution> _sets;

    public InMemoryPermissionSetResolver(IEnumerable<PermissionSetResolution> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        _sets = sets.ToDictionary(set => set.Nsid, StringComparer.Ordinal);
    }

    public ValueTask<PermissionSetResolution?> ResolveAsync(
        PermissionSetRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_sets.GetValueOrDefault(request.Nsid));
}

/// <summary>Raised when an OAuth scope cannot be safely normalized into a grant snapshot.</summary>
public sealed class PermissionScopeException(string message) : Exception(message);

/// <summary>Parses direct permission scopes and expands <c>include:</c> permission sets.</summary>
public static class PermissionScopeResolver
{
    private static readonly PermissionAction[] AllActions =
        [PermissionAction.Create, PermissionAction.Update, PermissionAction.Delete];

    public static Task<PermissionSnapshot> ResolveAsync(
        string scope,
        IPermissionSetResolver permissionSets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(permissionSets);
        return ResolveCoreAsync(scope, permissionSets, now, cancellationToken);
    }

    /// <summary>Parses an already-expanded access-token scope without resolving remote sets.</summary>
    public static PermissionSnapshot ParseAccessTokenScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return ResolveCoreAsync(scope, new UnavailablePermissionSetResolver(), DateTimeOffset.UtcNow, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static async Task<PermissionSnapshot> ResolveCoreAsync(
        string scope,
        IPermissionSetResolver permissionSets,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool transition = false;
        var grants = new List<PermissionGrant>();

        foreach (string token in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "atproto")
                continue;
            if (token == "transition:generic")
            {
                transition = true;
                continue;
            }
            if (token.StartsWith("include:", StringComparison.Ordinal))
            {
                PermissionSetRequest request = ParseInclude(token);
                PermissionSetResolution resolution = await permissionSets.ResolveAsync(request, cancellationToken)
                    ?? throw new PermissionScopeException($"permission set '{request.Nsid}' could not be resolved");
                ExpandPermissionSet(request, resolution, now, grants);
                continue;
            }
            grants.Add(ParseDirectRepoScope(token));
        }

        return transition || grants.Count > 0
            ? new PermissionSnapshot(transition, grants)
            : PermissionSnapshot.Empty;
    }

    private static PermissionSetRequest ParseInclude(string token)
    {
        string raw = token["include:".Length..];
        (string positional, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters) = ParseParts(raw, "permission-set");
        if (!IsNsid(positional))
            throw new PermissionScopeException($"invalid permission-set NSID '{positional}'");

        foreach (string parameter in parameters.Keys)
            if (parameter is not "aud")
                throw new PermissionScopeException($"unknown include parameter '{parameter}'");
        return new PermissionSetRequest(positional, parameters);
    }

    private static void ExpandPermissionSet(
        PermissionSetRequest request,
        PermissionSetResolution resolution,
        DateTimeOffset now,
        ICollection<PermissionGrant> grants)
    {
        if (!string.Equals(request.Nsid, resolution.Nsid, StringComparison.Ordinal))
            throw new PermissionScopeException("permission-set resolver returned a mismatched NSID");
        if (resolution.ExpiresAt is { } expiresAt && expiresAt <= now)
            throw new PermissionScopeException($"permission set '{request.Nsid}' has expired");
        if (resolution.StaleAt is { } staleAt && staleAt <= now)
            throw new PermissionScopeException($"permission set '{request.Nsid}' is stale");

        string authority = request.Nsid[..request.Nsid.LastIndexOf('.')];
        foreach (PermissionDeclaration declaration in resolution.Permissions)
        {
            if (!string.Equals(declaration.Resource, "repo", StringComparison.Ordinal))
                throw new PermissionScopeException($"unknown permission resource '{declaration.Resource}'");
            if (declaration.Parameters.Keys.Any(key => key is not ("collection" or "action")))
                throw new PermissionScopeException("permission-set repo declaration contains an unknown parameter");

            if (!declaration.Parameters.TryGetValue("collection", out IReadOnlyList<string>? collections)
                || collections.Count == 0)
                throw new PermissionScopeException("permission-set repo declaration requires collection");
            if (collections.Any(collection => collection == "*" || !IsNsid(collection)))
                throw new PermissionScopeException("permission-set collections must be concrete NSIDs");
            if (collections.Any(collection => !IsWithinAuthority(collection, authority)))
                throw new PermissionScopeException("permission-set collection exceeds the set namespace");

            IReadOnlyList<string> actionNames = declaration.Parameters.GetValueOrDefault("action")
                ?? Array.Empty<string>();
            PermissionAction[] actions = actionNames.Count == 0
                ? AllActions
                : actionNames.Select(ParseAction).ToArray();
            if (actionNames.Count != actions.Distinct().Count())
                throw new PermissionScopeException("permission-set actions must be unique");

            foreach (string collection in collections)
                grants.Add(new PermissionGrant(collection, actions));
        }
    }

    private static PermissionGrant ParseDirectRepoScope(string token)
    {
        if (!token.StartsWith("repo", StringComparison.Ordinal))
            throw new PermissionScopeException($"unsupported permission scope '{token}'");

        string raw = token["repo".Length..];
        (string positional, IReadOnlyDictionary<string, IReadOnlyList<string>> parameters) = ParseParts(raw, "repo");
        string collection;
        if (positional.Length > 0)
        {
            if (!positional.StartsWith(':'))
                throw new PermissionScopeException($"invalid repo permission scope '{token}'");
            collection = Decode(positional[1..]);
            if (parameters.ContainsKey("collection"))
                throw new PermissionScopeException("repo collection cannot be declared twice");
        }
        else if (parameters.TryGetValue("collection", out IReadOnlyList<string>? collections)
            && collections.Count == 1)
        {
            collection = collections[0];
        }
        else
        {
            throw new PermissionScopeException("repo permission requires collection");
        }

        if (collection != "*" && !IsNsid(collection))
            throw new PermissionScopeException($"invalid repo collection '{collection}'");
        if (collection.IndexOf('*') >= 0 && collection != "*")
            throw new PermissionScopeException("partial collection wildcards are not supported");

        foreach (string parameter in parameters.Keys)
            if (parameter is not ("collection" or "action"))
                throw new PermissionScopeException($"unknown repo permission parameter '{parameter}'");

        IReadOnlyList<string> actionNames = parameters.GetValueOrDefault("action")
            ?? Array.Empty<string>();
        PermissionAction[] actions = actionNames.Count == 0
            ? AllActions
            : actionNames.Select(ParseAction).ToArray();
        if (actionNames.Count != actions.Distinct().Count())
            throw new PermissionScopeException("repo actions must be unique");
        return new PermissionGrant(collection, actions);
    }

    private static PermissionAction ParseAction(string value) =>
        PermissionSnapshot.TryParseAction(value, out PermissionAction action)
            ? action
            : throw new PermissionScopeException($"unknown repo action '{value}'");

    private static (string Positional, IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters) ParseParts(
        string raw,
        string resource)
    {
        int queryIndex = raw.IndexOf('?');
        string positional = queryIndex < 0 ? raw : raw[..queryIndex];
        string query = queryIndex < 0 ? string.Empty : raw[(queryIndex + 1)..];
        var parameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (query.Length > 0)
        {
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0)
                    throw new PermissionScopeException($"invalid {resource} parameter '{pair}'");
                string name = Decode(pair[..equals]);
                string value = Decode(pair[(equals + 1)..]);
                if (name.Length == 0 || value.Length == 0)
                    throw new PermissionScopeException($"invalid {resource} parameter '{pair}'");
                if (!parameters.TryGetValue(name, out List<string>? values))
                {
                    values = [];
                    parameters.Add(name, values);
                }
                values.Add(value);
            }
        }

        return (positional, parameters.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.Ordinal));
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException ex)
        {
            throw new PermissionScopeException($"invalid percent encoding in permission scope: {ex.Message}");
        }
    }

    private static bool IsWithinAuthority(string collection, string authority) =>
        collection.StartsWith(authority + ".", StringComparison.Ordinal)
        && collection.Length > authority.Length + 1;

    private static bool IsNsid(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 317)
            return false;
        string[] segments = value.Split('.');
        if (segments.Length < 3)
            return false;

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length is 0 or > 63)
                return false;
            bool name = i == segments.Length - 1;
            if (!char.IsAsciiLetter(segment[0]))
                return false;
            if (!name && (segment[0] == '-' || segment[^1] == '-'))
                return false;
            if (!name && char.IsAsciiDigit(segment[0]))
                return false;
            if (name && !char.IsAsciiLetter(segment[0]))
                return false;
            foreach (char c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c) && (!name && c == '-'))
                    return false;
            }
        }
        return true;
    }
}

/// <summary>Stable JSON encoding for the persisted permission snapshot.</summary>
public static class PermissionSnapshotCodec
{
    public static string Serialize(PermissionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var grants = snapshot.Grants.Select(grant => new
        {
            collection = grant.Collection,
            actions = grant.Actions.OrderBy(PermissionSnapshot.ActionName).Select(PermissionSnapshot.ActionName).ToArray(),
        });
        return JsonSerializer.Serialize(new
        {
            transitionGeneric = snapshot.AllowsTransitionGeneric,
            grants,
        });
    }

    public static PermissionSnapshot Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return PermissionSnapshot.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            bool transition = root.TryGetProperty("transitionGeneric", out JsonElement transitionElement)
                && transitionElement.ValueKind == JsonValueKind.True;
            var grants = new List<PermissionGrant>();
            if (root.TryGetProperty("grants", out JsonElement grantsElement))
            {
                if (grantsElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("permission snapshot grants must be an array");
                foreach (JsonElement element in grantsElement.EnumerateArray())
                {
                    string collection = element.GetProperty("collection").GetString()
                        ?? throw new InvalidDataException("permission snapshot collection is required");
                    var actions = new List<PermissionAction>();
                    foreach (JsonElement action in element.GetProperty("actions").EnumerateArray())
                        actions.Add(ParseAction(action.GetString()));
                    grants.Add(new PermissionGrant(collection, actions));
                }
            }
            return transition || grants.Count > 0
                ? new PermissionSnapshot(transition, grants)
                : PermissionSnapshot.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("permission snapshot is not valid JSON", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("permission snapshot is missing a required field", ex);
        }
    }

    private static PermissionAction ParseAction(string? value) =>
        value is not null && PermissionSnapshot.TryParseAction(value, out PermissionAction action)
            ? action
            : throw new InvalidDataException($"unknown permission snapshot action '{value}'");
}
