using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;

namespace Agentry.Generator;

/// <summary>
/// Builds a JSON Schema for a method's parameters, at compile time.
/// </summary>
/// <remarks>
/// <para>
/// This is the differentiating piece. The usual way to get a tool schema is to
/// reflect over the method at startup — which is what
/// <c>AIFunctionFactory.Create</c> does, and it works until somebody publishes
/// trimmed and the parameter metadata is gone. Here the schema is a string
/// literal in the emitted file: nothing to trim, nothing to reflect over, and
/// it is visible in review.
/// </para>
/// <para>
/// The supported set is deliberately small. A type this cannot describe is
/// <c>AGT005</c> at build rather than a tool the model calls wrongly at run
/// time — and "the model sent a shape my parameter could not take" is a bad
/// thing to learn from a production trace.
/// </para>
/// </remarks>
internal static class SchemaWriter
{
    /// <summary>The schema, or <c>null</c> when a parameter cannot be described.</summary>
    public static string? TryWrite(IMethodSymbol method, out IParameterSymbol? unsupported)
    {
        unsupported = null;

        var properties = new StringBuilder();
        var required = new StringBuilder();
        var first = true;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken") continue;

            var type = JsonTypeFor(parameter.Type);
            if (type is null)
            {
                unsupported = parameter;
                return null;
            }

            if (!first)
            {
                properties.Append(',');
                required.Append(',');
            }

            properties.Append('"').Append(parameter.Name).Append("\":{\"type\":\"").Append(type).Append("\"}");
            required.Append('"').Append(parameter.Name).Append('"');
            first = false;
        }

        return $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}],\"additionalProperties\":false}}";
    }

    /// <summary>
    /// The JSON type for a parameter, or <c>null</c> when there isn't one.
    /// </summary>
    /// <remarks>
    /// Arrays of a supported scalar are allowed because "give me several" is the
    /// commonest shape a tool actually needs. Nested objects are not, yet:
    /// describing them means walking the type graph and deciding what to do
    /// about cycles, and a half-correct object schema is worse than a build
    /// error that says to flatten the parameter.
    /// </remarks>
    private static string? JsonTypeFor(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => "string",
        SpecialType.System_Boolean => "boolean",
        SpecialType.System_Int32 or SpecialType.System_Int64 => "integer",
        SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal => "number",
        _ when type is IArrayTypeSymbol array && JsonTypeFor(array.ElementType) is not null => "array",
        _ when type.TypeKind == TypeKind.Enum => "string",
        _ => null,
    };
}
