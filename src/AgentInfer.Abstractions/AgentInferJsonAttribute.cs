using System;

namespace AgentInfer;

/// <summary>
/// Names the <c>JsonSerializerContext</c> this assembly's generated contracts
/// should bind through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why you declare the context and the generator only points at it.</b>
/// Roslyn source generators do not chain: one generator's output is never
/// another's input. A <c>JsonSerializerContext</c> emitted by AgentInfer would be
/// invisible to <c>System.Text.Json</c>'s generator, so it would compile to an
/// abstract class with no metadata in it — verified, and the error is
/// "does not implement inherited abstract member GetTypeInfo(Type)".
/// </para>
/// <para>
/// What does work is the other direction: two generators' <em>outputs</em> land
/// in the same compilation, so generated code may reference generated code. You
/// declare six lines of context, STJ's generator fills it in, and AgentInfer's
/// generator emits contracts that reach into it.
/// </para>
/// <para>
/// Absent, the generated agent keeps the reflective path — which still works,
/// and still carries <c>[RequiresUnreferencedCode]</c>. So this attribute is
/// how an assembly opts into being trimmable, rather than something everyone
/// has to write.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [JsonSourceGenerationOptions(
///     PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
///     UseStringEnumConverter = true,
///     RespectNullableAnnotations = true,
///     RespectRequiredConstructorParameters = true)]
/// [JsonSerializable(typeof(Verdict))]
/// internal partial class LedgerJson : JsonSerializerContext;
///
/// [assembly: AgentInferJson(typeof(LedgerJson))]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class AgentInferJsonAttribute : Attribute
{
    public AgentInferJsonAttribute(Type context) => Context = context;

    /// <summary>The partial class deriving from <c>JsonSerializerContext</c>.</summary>
    public Type Context { get; }
}
