using System.Collections.Generic;
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
    /// <summary>
    /// A JSON Schema describing a return type, or <c>null</c> if it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This closes the gap that made the library's claim half-true. Tool
    /// <em>parameters</em> got a compile-time schema; return types did not — so
    /// a model asked for a <c>Verdict(bool, int, string[])</c> was told only
    /// "reply with JSON" and guessed. A real run answered
    /// <c>{"supported": true}</c>, which is a reasonable invention given no
    /// schema and nothing like the type.
    /// </para>
    /// <para>
    /// Names are camelCased to match <c>JsonSerializerOptions.Web</c>, which is
    /// what binds the reply. A schema that disagrees with the binder is worse
    /// than none: the model obeys it and the bind still fails.
    /// </para>
    /// </remarks>
    public static string? TryWriteReturn(ITypeSymbol type) =>
        Describe(type, new HashSet<string>(StringComparer.Ordinal), depth: 0);

    /// <summary>
    /// One type as JSON Schema. Depth-limited, and cycle-guarded by name.
    /// </summary>
    /// <remarks>
    /// A depth cap rather than full generality. Three levels covers the shapes
    /// a model can actually be asked to produce reliably, and a schema deep
    /// enough to need recursion is a signal the return type is too big to ask a
    /// model for in one go.
    /// </remarks>
    private static string? Describe(ITypeSymbol type, HashSet<string> seen, int depth)
    {
        if (depth > 3) return null;

        if (JsonTypeFor(type) is { } scalar && scalar != "array")
        {
            return $"{{\"type\":\"{scalar}\"}}";
        }

        if (type is IArrayTypeSymbol array)
        {
            var items = Describe(array.ElementType, seen, depth + 1);
            return items is null ? null : $"{{\"type\":\"array\",\"items\":{items}}}";
        }

        if (ElementOfList(type) is { } element)
        {
            var items = Describe(element, seen, depth + 1);
            return items is null ? null : $"{{\"type\":\"array\",\"items\":{items}}}";
        }

        if (type is not INamedTypeSymbol named || named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        var key = named.ToDisplayString();
        if (!seen.Add(key)) return null;

        try
        {
            var properties = new StringBuilder();
            var required = new StringBuilder();
            var first = true;

            foreach (var member in named.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic) continue;
                if (member.Name == "EqualityContract") continue;   // records carry this

                var described = Describe(member.Type, seen, depth + 1);
                if (described is null) return null;

                if (!first)
                {
                    properties.Append(',');
                    required.Append(',');
                }

                var name = Camel(member.Name);
                properties.Append('"').Append(name).Append("\":").Append(described);
                required.Append('"').Append(name).Append('"');
                first = false;
            }

            return first
                ? null   // no properties is not a shape worth asking a model for
                : $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}],\"additionalProperties\":false}}";
        }
        finally
        {
            seen.Remove(key);
        }
    }

    /// <summary>The element of an <c>IEnumerable&lt;T&gt;</c>-shaped type.</summary>
    private static ITypeSymbol? ElementOfList(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        named.AllInterfaces.Concat([named]).Any(i => i.ToDisplayString().StartsWith("System.Collections.Generic.IEnumerable<", StringComparison.Ordinal))
            ? named.TypeArguments.FirstOrDefault()
            : null;

    private static string Camel(string name) =>
        name.Length > 0 && char.IsUpper(name[0])
            ? char.ToLowerInvariant(name[0]) + name.Substring(1)
            : name;

    /// <summary>
    /// The <c>JsonElement</c> accessor for a parameter, chosen at compile time.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="JsonTypeFor"/> and kept beside it deliberately: a
    /// type that gains a schema entry but no reader would emit dispatch that
    /// does not compile, and the two drifting apart is the obvious way for that
    /// to happen.
    /// </remarks>
    public static string? ReaderFor(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => "GetString()!",
        SpecialType.System_Boolean => "GetBoolean()",
        SpecialType.System_Int32 => "GetInt32()",
        SpecialType.System_Int64 => "GetInt64()",
        SpecialType.System_Double => "GetDouble()",
        SpecialType.System_Single => "GetSingle()",
        SpecialType.System_Decimal => "GetDecimal()",
        _ when type is IArrayTypeSymbol array && ReaderFor(array.ElementType) is { } element =>
            $"EnumerateArray().Select(e => e.{element}).ToArray()",
        _ when type.TypeKind == TypeKind.Enum =>
            $"GetString() is {{ }} s ? ({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})global::System.Enum.Parse(typeof({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}), s, true) : default",
        _ => null,
    };

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
