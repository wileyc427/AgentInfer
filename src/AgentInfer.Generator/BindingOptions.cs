using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace AgentInfer.Generator;

/// <summary>
/// Checks a declared <c>JsonSerializerContext</c> against the options the
/// reflective path binds with.
/// </summary>
/// <remarks>
/// <para>
/// The two halves are written in different files, by different people, at
/// different times, and nothing but this connects them. When they disagree the
/// same reply binds one way through <c>CompleteJsonReflectivelyAsync</c> and
/// another through an <c>IReplyContract</c> — which is the one thing the
/// remarks on every context in this repository say must not happen, and which
/// was true of all of them until it was looked for.
/// </para>
/// <para>
/// The other half lives in <c>AgentJson.Default</c> and <c>AgentJson.Binding()</c>
/// in the runtime assembly, which the generator cannot reference: it targets
/// netstandard2.0 and loads into the compiler. So the list below is a copy, and
/// the only one — a property added there and not here makes this check quieter,
/// never wrong.
/// </para>
/// </remarks>
internal static class BindingOptions
{
    /// <summary>
    /// <c>JsonKnownNamingPolicy.CamelCase</c> and
    /// <c>JsonNumberHandling.AllowReadingFromString</c>, by value.
    /// </summary>
    /// <remarks>
    /// Read as integers because that is what a <c>TypedConstant</c> carries for
    /// an enum. Both are public framework enums whose members are fixed by
    /// compatibility, so the numbers cannot drift.
    /// </remarks>
    private const int CamelCase = 1;
    private const int AllowReadingFromString = 1;

    /// <param name="Name">The named argument on <c>[JsonSourceGenerationOptions]</c>.</param>
    /// <param name="Text">How to write it, for the message.</param>
    /// <param name="IsSatisfied">Whether the declared value agrees.</param>
    private sealed record Option(string Name, string Text, Func<object?, bool> IsSatisfied);

    private static readonly Option[] Required =
    [
        new("PropertyNamingPolicy",
            "PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase",
            value => value is int policy && policy == CamelCase),

        new("PropertyNameCaseInsensitive",
            "PropertyNameCaseInsensitive = true",
            value => value is true),

        // A flag, so any value that includes it agrees. Writing numbers as
        // strings as well is a wider tolerance, not a different one.
        new("NumberHandling",
            "NumberHandling = JsonNumberHandling.AllowReadingFromString",
            value => value is int handling && (handling & AllowReadingFromString) != 0),

        new("UseStringEnumConverter",
            "UseStringEnumConverter = true",
            value => value is true),

        new("RespectNullableAnnotations",
            "RespectNullableAnnotations = true",
            value => value is true),

        new("RespectRequiredConstructorParameters",
            "RespectRequiredConstructorParameters = true",
            value => value is true),
    ];

    private const string OptionsAttribute =
        "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute";

    /// <summary>What this context leaves unset or sets differently, in order.</summary>
    public static ImmutableArray<string> MissingFrom(INamedTypeSymbol context)
    {
        var declared = Declared(context);

        return
        [
            .. Required
                .Where(option => !option.IsSatisfied(
                    declared.TryGetValue(option.Name, out var value) ? value : null))
                .Select(option => option.Text)
        ];
    }

    private static Dictionary<string, object?> Declared(INamedTypeSymbol context)
    {
        var declared = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var attribute in context.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != OptionsAttribute) continue;

            foreach (var argument in attribute.NamedArguments)
            {
                declared[argument.Key] = argument.Value.Value;
            }
        }

        return declared;
    }
}
