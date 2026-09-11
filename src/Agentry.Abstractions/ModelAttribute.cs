namespace Agentry;

/// <summary>
/// Runs this method against a named model role rather than the agent's default.
/// </summary>
/// <remarks>
/// <para>
/// Two methods on one interface can want very different models. A method that
/// has to reason over figures and a method that classifies a request into one
/// of four words are not the same problem, and paying for the harder one twice
/// — or failing the harder one to save money on the easier — is a false
/// economy that shows up as intermittent wrong answers.
/// </para>
/// <para>
/// <b>It names a role, not a model.</b> <c>[Model("accurate")]</c>, never
/// <c>[Model("claude-sonnet-5")]</c>. A domain assembly should not carry vendor
/// model ids: the mapping differs between a laptop and production, changes when
/// a model is deprecated, and is configuration rather than design. The same
/// instinct as <see cref="RequiresPermissionAttribute"/> naming a permission
/// instead of a list of people.
/// </para>
/// <para>
/// The role is resolved by an <c>IModelRouter</c> the agent is constructed
/// with. A method with no attribute uses the agent's default runner, so the
/// common case stays a single dependency.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ModelAttribute : Attribute
{
    /// <param name="role">
    /// What this method needs, in your words — "accurate", "cheap", "local".
    /// </param>
    public ModelAttribute(string role) => Role = role;

    public string Role { get; }
}
