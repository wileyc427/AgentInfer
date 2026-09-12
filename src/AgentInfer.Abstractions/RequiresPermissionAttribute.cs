namespace AgentInfer;

/// <summary>
/// The permission a caller must hold before this tool may be reached.
/// </summary>
/// <remarks>
/// <para>
/// Read by the generator, which emits one narrowed facade per permission set.
/// At run time only the facade matching the principal is bound, so a method the
/// caller may not use is not merely refused — it is absent.
/// </para>
/// <para>
/// That distinction is the whole design. The Python framework this follows has
/// a <c>@hidden</c> decorator that keeps a method out of the generated
/// documentation while leaving it perfectly callable, because an in-process
/// object cannot make its own methods unreachable. A generated facade has no
/// such method to reach: capability, not a check.
/// </para>
/// <para>Reserved for P2.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class RequiresPermissionAttribute : Attribute
{
    public RequiresPermissionAttribute(string permission) => Permission = permission;

    public string Permission { get; }
}
