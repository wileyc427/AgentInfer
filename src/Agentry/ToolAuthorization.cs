using System.Text.Json;

namespace Agentry;

/// <summary>What the current caller is allowed to do.</summary>
/// <remarks>
/// <para>
/// Deliberately not <c>ClaimsPrincipal</c> or <c>IAuthorizationService</c>.
/// Those live in ASP.NET Core, and a console app or a worker should be able to
/// use this library without taking the web stack. The adapter that maps this
/// onto <c>IAuthorizationService</c> is three lines and belongs in a separate
/// package.
/// </para>
/// <para>
/// Synchronous on purpose: this is consulted once per tool per turn, and an
/// async permission check invites a database round trip inside a loop that is
/// already making model calls.
/// </para>
/// </remarks>
public interface IToolAuthorizer
{
    public bool IsGranted(string permission);
}

/// <summary>Grants everything. For tests and single-user tools.</summary>
/// <remarks>
/// Named for what it does rather than something reassuring like
/// <c>DefaultAuthorizer</c>, because it appears in registration code and
/// somebody should notice it there.
/// </remarks>
public sealed class GrantAllTools : IToolAuthorizer
{
    public static GrantAllTools Instance { get; } = new();

    public bool IsGranted(string permission) => true;
}

/// <summary>Grants exactly the permissions it was given.</summary>
public sealed class GrantedPermissions(IEnumerable<string> permissions) : IToolAuthorizer
{
    private readonly HashSet<string> _granted = new(permissions, StringComparer.Ordinal);

    public bool IsGranted(string permission) => _granted.Contains(permission);
}

/// <summary>Thrown when a tool is invoked that the caller may not use.</summary>
public sealed class ToolDeniedException(string tool, string permission)
    : Exception($"'{tool}' requires '{permission}', which this caller does not hold.")
{
    public string Tool { get; } = tool;

    public string Permission { get; } = permission;
}

/// <summary>
/// Dispatches a tool call by name. The generated half of the tool surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Access is enforced in two places and that is not redundancy.</b>
/// <see cref="AvailableTo"/> filters the menu the model is sent, so a tool the
/// caller may not use is one the model is never told about and cannot decide to
/// want. <see cref="InvokeAsync"/> checks again, because a conversation that
/// began before a permission changed still has the old tool written down in its
/// context and will happily call it.
/// </para>
/// <para>
/// This is a change from the original design note, which proposed generating one
/// narrowed facade <em>type</em> per permission set. That is combinatorial — the
/// distinct sets a principal can hold is the powerset of the permissions in
/// play, so eight permissions is 256 generated types. Filtering the menu and
/// gating dispatch gets the same property (a tool you may not use is absent from
/// what the model sees) without the explosion.
/// </para>
/// <para>
/// The dispatch itself is generated: a switch on the name, with arguments
/// deserialized by code that knows their static types. Nothing here reflects
/// over a method.
/// </para>
/// </remarks>
public abstract class ToolInvoker
{
    /// <summary>Every tool this invoker can dispatch, regardless of permission.</summary>
    public abstract ToolManifest Manifest { get; }

    /// <summary>The subset this caller may use.</summary>
    public IReadOnlyList<ToolDescriptor> AvailableTo(IToolAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(authorizer);

        return [.. Manifest.Tools.Where(tool => Permits(tool, authorizer) is null)];
    }

    /// <summary>Runs a tool by name, after checking the caller may.</summary>
    /// <param name="arguments">JSON object of arguments, as the model produced it.</param>
    /// <returns>The result, rendered for the model.</returns>
    public async Task<string> InvokeAsync(
        string name,
        string arguments,
        IToolAuthorizer authorizer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorizer);

        var tool = Manifest.Tools.FirstOrDefault(t => t.Name == name)
            ?? throw new ToolDeniedException(name, "(no such tool)");

        if (Permits(tool, authorizer) is { } missing)
        {
            throw new ToolDeniedException(name, missing);
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        return await DispatchAsync(name, document.RootElement, ct).ConfigureAwait(false);
    }

    /// <summary>Generated. A switch on the name, with typed argument binding.</summary>
    protected abstract Task<string> DispatchAsync(string name, JsonElement arguments, CancellationToken ct);

    /// <summary>The first permission this caller is missing, or null.</summary>
    /// <remarks>
    /// Every declared permission is required, not any. A tool marked both
    /// <c>ledger.read</c> and <c>ledger.write</c> needs both — the reading of
    /// "requires" that cannot surprise somebody in the direction that matters.
    /// </remarks>
    private static string? Permits(ToolDescriptor tool, IToolAuthorizer authorizer)
    {
        foreach (var permission in tool.Permissions)
        {
            if (!authorizer.IsGranted(permission)) return permission;
        }

        return null;
    }
}
