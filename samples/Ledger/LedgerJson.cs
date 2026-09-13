using System.Collections.Generic;
using System.Text.Json.Serialization;

using AgentInfer;

// Six lines of context, and the generated agent's typed path stops being
// reflective. The attribute names it; the generator points at it.
//
// It has to be declared here rather than emitted, because Roslyn generators do
// not chain: a context AgentInfer wrote would be invisible to System.Text.Json's
// generator and would compile to an abstract class with no metadata in it.
// Their outputs can reference each other; only their inputs cannot.
[assembly: AgentInferJson(typeof(Ledger.LedgerJson))]

namespace Ledger;

/// <summary>
/// Binding metadata for every type crossing the wire: what a model must
/// produce, and what a tool hands back to it.
/// </summary>
/// <remarks>
/// <para>
/// The options mirror what the reflective path set, and they have to: a
/// context that camelCased differently, or did not respect required
/// constructor parameters, would bind the same reply differently depending on
/// which overload a method happened to take. What they mirror is
/// <c>AgentJson.Default</c> and <c>AgentJson.Binding()</c>; nothing checks the
/// two agree, so a property added there belongs here in the same change.
/// </para>
/// <para>
/// <c>IReadOnlyList&lt;CategorySummary&gt;</c> is here because
/// <c>LedgerTools.Overview</c> returns it, and AIN016 said so at build. A tool
/// result richer than a scalar or an array of them has to come from somewhere
/// static, and this is the somewhere.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Verdict))]
[JsonSerializable(typeof(IReadOnlyList<CategorySummary>))]
internal partial class LedgerJson : JsonSerializerContext;
