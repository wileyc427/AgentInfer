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
    private const string FlagsAttribute = "System.FlagsAttribute";

    /// <summary>
    /// The attribute that renames one enum member on the wire.
    /// </summary>
    /// <remarks>
    /// Matched by metadata name rather than referenced, because this assembly
    /// is netstandard2.0 and the attribute is .NET 9. Honouring it is not
    /// optional: the binder obeys it, so a schema that ignored it would name
    /// values the binder then rejects — the exact disagreement this file
    /// exists to prevent.
    /// </remarks>
    private const string EnumMemberNameAttribute =
        "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute";

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

            var schema = ParameterSchema(parameter.Type);
            if (schema is null)
            {
                unsupported = parameter;
                return null;
            }

            if (!first)
            {
                properties.Append(',');
                required.Append(',');
            }

            properties.Append('"').Append(parameter.Name).Append("\":").Append(schema);
            required.Append('"').Append(parameter.Name).Append('"');
            first = false;
        }

        return $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}],\"additionalProperties\":false}}";
    }

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
    /// The value checks a return type declares, as lines of C#.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the same <c>[Range]</c> and <c>[MaxLength]</c> attributes that
    /// produce the schema, in the same pass — which is the whole point. The
    /// schema tells the model the bound and this enforces it, and because both
    /// come from one read of one attribute they cannot disagree. They did
    /// disagree before: a model answered <c>score: 100</c> to a field meant to
    /// be 1–5 and bound cleanly, because 100 is a perfectly good integer.
    /// </para>
    /// <para>
    /// Emitted as text rather than checked reflectively, because
    /// <c>Validator.TryValidateObject</c> walks the instance's properties and
    /// trimming can leave it walking nothing — a check that reports success
    /// because it found nothing to check.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> ReturnChecks(ITypeSymbol type)
    {
        if (Unwrap(type) is not INamedTypeSymbol named) yield break;

        foreach (var member in named.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic) continue;
            if (member.Name == "EqualityContract") continue;

            foreach (var attribute in member.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                var arguments = attribute.ConstructorArguments;

                if (name == "System.ComponentModel.DataAnnotations.RangeAttribute" && arguments.Length >= 2)
                {
                    var min = Invariant(arguments[0].Value!);
                    var max = Invariant(arguments[1].Value!);

                    yield return "if (value." + member.Name + " is < " + min + " or > " + max + ") "
                        + "return $\"" + Camel(member.Name) + " must be between " + min + " and " + max
                        + ", not {value." + member.Name + "}\";";
                }
                else if (name == "System.ComponentModel.DataAnnotations.MinLengthAttribute" && arguments.Length >= 1)
                {
                    var min = Invariant(arguments[0].Value!);

                    yield return "if (value." + member.Name + ".Length < " + min + ") "
                        + "return \"" + Camel(member.Name) + " must be at least " + min + " long\";";
                }
                else if (name == "System.ComponentModel.DataAnnotations.MaxLengthAttribute" && arguments.Length >= 1)
                {
                    var max = Invariant(arguments[0].Value!);

                    yield return "if (value." + member.Name + ".Length > " + max + ") "
                        + "return \"" + Camel(member.Name) + " must be at most " + max + " long\";";
                }
            }
        }
    }

    /// <summary>
    /// The first <c>[Flags]</c> enum reachable in this type, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A flags enum is a set, and <c>{"enum":[…]}</c> describes a choice of one.
    /// Emitting the member list would tell the model it may pick exactly one
    /// value of a type whose whole purpose is combining them, and the reply
    /// <c>"Read, Write"</c> then fails at the binder with a message about an
    /// unrecognised value. Naming it at build time is <c>AGT008</c>.
    /// </remarks>
    public static INamedTypeSymbol? FlagsEnumIn(ITypeSymbol type) =>
        FlagsEnumIn(type, new HashSet<string>(StringComparer.Ordinal), depth: 0);

    private static INamedTypeSymbol? FlagsEnumIn(ITypeSymbol type, HashSet<string> seen, int depth)
    {
        if (depth > 3) return null;

        type = Unwrap(type);

        if (type.TypeKind == TypeKind.Enum)
        {
            return IsFlags(type) ? type as INamedTypeSymbol : null;
        }

        if (type is IArrayTypeSymbol array) return FlagsEnumIn(array.ElementType, seen, depth + 1);
        if (ElementOfList(type) is { } element) return FlagsEnumIn(element, seen, depth + 1);

        if (type is not INamedTypeSymbol named || named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        var key = named.ToDisplayString();
        if (!seen.Add(key)) return null;

        try
        {
            foreach (var member in named.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic) continue;
                if (member.Name == "EqualityContract") continue;

                if (FlagsEnumIn(member.Type, seen, depth + 1) is { } found) return found;
            }

            return null;
        }
        finally
        {
            seen.Remove(key);
        }
    }

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

        // Nullable<T> first, and not as a tidy-up. Without it the walk below
        // reaches Nullable<Urgency> as an ordinary struct and describes its
        // members: a model asked for a Task<Urgency?> was told to reply with
        // {"hasValue":…,"value":…}, which is a shape nothing wants and no
        // reply could usefully satisfy.
        type = Unwrap(type);

        if (ScalarSchema(type) is { } scalar) return scalar;

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

                described = WithConstraints(described, member);

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

    /// <summary>
    /// Folds DataAnnotations on a property into its schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schema of <c>{"type":"integer"}</c> is a shape and not a contract. A
    /// real run answered <c>score: 100</c> to a field meant to be 1–5, and it
    /// bound cleanly, because 100 is a perfectly good integer.
    /// </para>
    /// <para>
    /// DataAnnotations rather than a vocabulary of our own: <c>[Range]</c> is
    /// already what a .NET developer reaches for, it is already understood by
    /// model binding and validation, and the same attribute now does double
    /// duty — it tells the model the bound and it is checked after binding.
    /// </para>
    /// </remarks>
    private static string WithConstraints(string schema, IPropertySymbol property)
    {
        var extra = new StringBuilder();

        foreach (var attribute in property.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();

            switch (name)
            {
                case "System.ComponentModel.DataAnnotations.RangeAttribute"
                    when attribute.ConstructorArguments.Length >= 2:
                    Append(extra, "minimum", attribute.ConstructorArguments[0].Value);
                    Append(extra, "maximum", attribute.ConstructorArguments[1].Value);
                    break;

                case "System.ComponentModel.DataAnnotations.MinLengthAttribute"
                    when attribute.ConstructorArguments.Length >= 1:
                    Append(extra, LengthKeyword(property, "min"), attribute.ConstructorArguments[0].Value);
                    break;

                case "System.ComponentModel.DataAnnotations.MaxLengthAttribute"
                    when attribute.ConstructorArguments.Length >= 1:
                    Append(extra, LengthKeyword(property, "max"), attribute.ConstructorArguments[0].Value);
                    break;
            }
        }

        if (extra.Length == 0) return schema;

        // Splice before the closing brace: the schema is a flat object here, so
        // string surgery is honest rather than a shortcut around a parser.
        return schema.Substring(0, schema.Length - 1) + extra + "}";
    }

    private static void Append(StringBuilder target, string keyword, object? value)
    {
        if (value is null) return;
        target.Append(",\"").Append(keyword).Append("\":").Append(Invariant(value));
    }

    /// <summary>`minLength` for a string, `minItems` for a collection.</summary>
    private static string LengthKeyword(IPropertySymbol property, string prefix) =>
        property.Type.SpecialType == SpecialType.System_String
            ? prefix + "Length"
            : prefix + "Items";

    private static string Invariant(object value) =>
        value is bool flag
            ? (flag ? "true" : "false")
            : System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null";

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
    /// A tool parameter as JSON Schema: a scalar, an enum, or an array of one.
    /// </summary>
    /// <remarks>
    /// Nested objects are not allowed, yet: describing them means walking the
    /// type graph and deciding what to do about cycles, and a half-correct
    /// object schema is worse than a build error that says to flatten the
    /// parameter. Arrays of a supported scalar are allowed because "give me
    /// several" is the commonest shape a tool actually needs — and they carry
    /// their <c>items</c>, because <c>{"type":"array"}</c> alone tells a model
    /// nothing about what to put in it.
    /// </remarks>
    private static string? ParameterSchema(ITypeSymbol type)
    {
        type = Unwrap(type);

        if (ScalarSchema(type) is { } scalar) return scalar;

        if (type is IArrayTypeSymbol array && ScalarSchema(Unwrap(array.ElementType)) is { } items)
        {
            return $"{{\"type\":\"array\",\"items\":{items}}}";
        }

        return null;
    }

    /// <summary>
    /// A scalar or enum as JSON Schema, or <c>null</c> for anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An enum is <b>not</b> <c>{"type":"string"}</c>. That was the shape this
    /// emitted before, and it is the reason routing did not work: the model was
    /// told to reply with a string and never told which strings were legal, so
    /// it invented <c>"urgent"</c> for a type whose members are
    /// <c>Low/Normal/High</c> and the bind failed on a value that reads
    /// perfectly sensibly.
    /// </para>
    /// <para>
    /// The member list is the whole point of using an enum as a return type:
    /// it turns "classify this" into a closed set the compiler also knows
    /// about, so the caller's <c>switch</c> and the model's options are the
    /// same list.
    /// </para>
    /// </remarks>
    private static string? ScalarSchema(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            // A flags enum is a set, not a choice; AGT008 names it at build.
            if (IsFlags(type)) return null;

            var members = EnumWireNames(type).ToArray();
            if (members.Length == 0) return null;

            var values = string.Join(",", members.Select(m => $"\"{m}\""));
            return $"{{\"type\":\"string\",\"enum\":[{values}]}}";
        }

        return JsonTypeFor(type) is { } name ? $"{{\"type\":\"{name}\"}}" : null;
    }

    /// <summary>
    /// Every member of an enum, spelled as the binder will expect it.
    /// </summary>
    /// <remarks>
    /// camelCase to match the <c>JsonStringEnumConverter</c> the runtime binds
    /// with, and <c>[JsonStringEnumMemberName]</c> ahead of that, because the
    /// converter honours it and a schema that did not would name values the
    /// binder rejects.
    /// </remarks>
    private static IEnumerable<string> EnumWireNames(ITypeSymbol type) =>
        type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => field.IsConst && field.HasConstantValue)
            .Select(field => WireName(field));

    private static string WireName(IFieldSymbol field)
    {
        foreach (var attribute in field.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != EnumMemberNameAttribute) continue;

            if (attribute.ConstructorArguments.FirstOrDefault().Value is string given && given.Length > 0)
            {
                return given;
            }
        }

        return Camel(field.Name);
    }

    private static bool IsFlags(ITypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FlagsAttribute);

    /// <summary>The <c>T</c> of a <c>Nullable&lt;T&gt;</c>, or the type itself.</summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            ? named.TypeArguments[0]
            : type;

    /// <summary>
    /// The <c>JsonElement</c> accessor for a parameter, chosen at compile time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paired with <see cref="JsonTypeFor"/> and kept beside it deliberately: a
    /// type that gains a schema entry but no reader would emit dispatch that
    /// does not compile, and the two drifting apart is the obvious way for that
    /// to happen.
    /// </para>
    /// <para>
    /// An enum reads through a generated <c>switch</c> over the same wire names
    /// the schema lists, rather than through <c>Enum.Parse</c>. Two reasons,
    /// and the second is the one that matters: <c>Enum.Parse</c> is reflection
    /// on the path this library claims is free of it, and it knows nothing
    /// about <c>[JsonStringEnumMemberName]</c> — so a renamed member would be
    /// offered to the model and then refused on arrival.
    /// </para>
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
        _ when type.TypeKind == TypeKind.Enum && !IsFlags(type) => EnumReader(type),
        _ => null,
    };

    /// <summary>A <c>switch</c> from wire name to member, built at compile time.</summary>
    private static string EnumReader(ITypeSymbol type)
    {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var arms = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => field.IsConst && field.HasConstantValue)
            .Select(field => $"\"{WireName(field)}\" => {qualified}.{field.Name}");

        // The default arm throws rather than falling back to the zero value. A
        // tool argument the model invented should fail loudly at the boundary;
        // silently becoming whichever member happens to be 0 is how a wrong
        // call looks like a right one in a trace.
        return "GetString() switch { "
            + string.Join(", ", arms)
            + $", var other => throw new global::System.ArgumentException($\"'{{other}}' is not a valid {type.Name}.\") }}";
    }

    private static string? JsonTypeFor(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => "string",
        SpecialType.System_Boolean => "boolean",
        SpecialType.System_Int32 or SpecialType.System_Int64 => "integer",
        SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal => "number",
        _ => null,
    };
}
