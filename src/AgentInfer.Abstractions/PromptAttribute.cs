namespace AgentInfer;

/// <summary>
/// The task prompt for one generation method.
/// </summary>
/// <remarks>
/// Required on every method of an <see cref="AgentAttribute"/> interface. A
/// method without one is <c>AIN001</c> at build, rather than a method that
/// silently inherits the type's prompt and behaves almost right.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PromptAttribute : Attribute
{
    public PromptAttribute(string prompt) => Prompt = prompt;

    /// <summary>What the model is told this method should do.</summary>
    public string Prompt { get; }
}
