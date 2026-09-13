namespace AgentInfer;

/// <summary>
/// Marks a method as something an agent may call, and describes it for the model.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, one method at a time, written by hand. Nothing is exposed to a model
/// because it happens to be public: a public method is a contract with other
/// code, and that is a different proposition from a menu item handed to
/// something that is trying to be helpful.
/// </para>
/// <para>
/// The description is part of the API. It is the only thing the model reads
/// when deciding whether to call this, so a vague one produces an agent that
/// calls the wrong tool — a bug in the tool, filed against the tool.
/// </para>
/// <para>Reserved. Present now so the vocabulary is stable.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AgentToolAttribute : Attribute
{
    public AgentToolAttribute(string description) => Description = description;

    public string Description { get; }
}
