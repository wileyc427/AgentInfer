using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Agentry;

namespace Intake;

/// <summary>
/// The binding metadata, from System.Text.Json's own source generator.
/// </summary>
/// <remarks>
/// <para>
/// Note whose generator this is. <c>JsonSerializerContext</c> is emitted by
/// <c>System.Text.Json</c>, which ships in the SDK — nothing to do with
/// Agentry's generator, which this project does not use for its agent. Six
/// lines, and the reflective binding that made the typed path untrimmable is
/// gone.
/// </para>
/// <para>
/// Every option the reflective path set has a declarative equivalent here,
/// which matters because a context that disagreed with the old
/// <c>JsonSerializerOptions</c> would bind differently depending on which
/// overload a method happened to call.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Extract))]
[JsonSerializable(typeof(Category))]
internal partial class IntakeJson : JsonSerializerContext;

/// <summary>
/// What the model is told, how the reply is bound, and what its values must
/// satisfy — for one return type, in one place.
/// </summary>
/// <remarks>
/// Hand-written, and this is the honest cost of not generating. But note what
/// it is <em>not</em>: three separate things in three files that can drift.
/// The schema and the check below are two lines apart, so the class of bug
/// where a model is told 1–5 and validated against 1–10 has nowhere to hide.
/// </remarks>
internal sealed class ExtractContract : IReplyContract<Extract>
{
    public static ExtractContract Instance { get; } = new();

    public string Schema =>
        """
        {"type":"object","properties":{"customer":{"type":"string"},"category":{"type":"string","enum":["billing","technical","account","spam"]},"urgency":{"type":"integer","minimum":1,"maximum":5},"asks":{"type":"array","items":{"type":"string"}}},"required":["customer","category","urgency","asks"],"additionalProperties":false}
        """;

    public JsonTypeInfo<Extract> TypeInfo => IntakeJson.Default.Extract;

    /// <summary>
    /// The half DataAnnotations used to do, written out.
    /// </summary>
    /// <remarks>
    /// Four lines instead of <c>[Range(1, 5)]</c>, and they buy something the
    /// attribute could not: this runs identically in a trimmed build.
    /// <c>Validator.TryValidateObject</c> reflects over the instance's
    /// properties, so trimming can leave it enumerating nothing and reporting
    /// success — a check that fails open, on the value a model is most likely
    /// to get wrong.
    /// </remarks>
    public string? Validate(Extract value) => value switch
    {
        { Urgency: < 1 or > 5 } => $"urgency must be between 1 and 5, not {value.Urgency}",
        { Customer.Length: 0 } => "customer must not be empty",
        { Asks.Length: 0 } => "asks must list at least one thing the customer wants",
        _ => null,
    };
}

/// <summary>A closed set of words. The schema is the members, so it cannot drift.</summary>
internal sealed class CategoryContract : IReplyContract<Category>
{
    public static CategoryContract Instance { get; } = new();

    public string Schema => """{"type":"string","enum":["billing","technical","account","spam"]}""";

    public JsonTypeInfo<Category> TypeInfo => IntakeJson.Default.Category;

    /// <summary>
    /// Nothing to check. Binding already refused anything that is not a member,
    /// which is what a closed return type buys.
    /// </summary>
    public string? Validate(Category value) => null;
}
