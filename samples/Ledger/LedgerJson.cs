using System.Text.Json.Serialization;

using Agentry;

// Six lines of context, and the generated agent's typed path stops being
// reflective. The attribute names it; the generator points at it.
//
// It has to be declared here rather than emitted, because Roslyn generators do
// not chain: a context Agentry wrote would be invisible to System.Text.Json's
// generator and would compile to an abstract class with no metadata in it.
// Their outputs can reference each other; only their inputs cannot.
[assembly: AgentryJson(typeof(Ledger.LedgerJson))]

namespace Ledger;

/// <summary>
/// Binding metadata for every type this app asks a model to produce.
/// </summary>
/// <remarks>
/// The options mirror what the reflective path set, and they have to: a
/// context that camelCased differently, or did not respect required
/// constructor parameters, would bind the same reply differently depending on
/// which overload a method happened to take.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Verdict))]
internal partial class LedgerJson : JsonSerializerContext;
