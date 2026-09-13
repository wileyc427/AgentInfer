using System;

namespace AgentInfer;

/// <summary>
/// Names the <c>JsonSerializerContext</c> this assembly's generated contracts
/// should bind through.
/// </summary>
/// <remarks>
/// <para>
/// You declare the context; the generator only points at it. Roslyn generators
/// do not chain — one generator's output is never another's input — so a
/// <c>JsonSerializerContext</c> emitted here would be invisible to
/// <c>System.Text.Json</c>'s generator and compile to an abstract class with no
/// metadata.
/// </para>
/// <para>
/// Generated code referencing generated code does work, since both land in the
/// same compilation. You declare the context, STJ's generator fills it in, and
/// this generator emits contracts that reach into it.
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
///     PropertyNameCaseInsensitive = true,
///     NumberHandling = JsonNumberHandling.AllowReadingFromString,
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
