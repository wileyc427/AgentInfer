using System.Text.Json;

using Microsoft.Extensions.AI;

using Xunit;

namespace Agentry.Tests;

/// <summary>
/// The model's arguments, on their way to a generated dispatcher.
/// </summary>
/// <remarks>
/// They used to go through <c>JsonSerializer.Serialize</c> over a
/// <c>Dictionary&lt;string, object?&gt;</c>, which discovers each value's type
/// at run time — reflection, on the one path this library says is free of it.
/// It survived because the analyzer that would have caught it was never on.
/// </remarks>
public sealed class ToolArgumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Echo : ToolInvoker
    {
        public string? Seen { get; private set; }

        public override ToolManifest Manifest { get; } = new(
        [
            new("Echo", "Echoes.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}", []),
        ]);

        protected override Task<string> DispatchAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Seen = arguments.GetRawText();
            return Task.FromResult("ok");
        }
    }

    private static async Task<string?> RoundTrip(Dictionary<string, object?> arguments)
    {
        var invoker = new Echo();
        var log = new ToolCallLog { Offered = 1, Total = 1 };
        var function = new GatedFunction(invoker.Manifest.Tools[0], invoker, GrantAllTools.Instance, log);

        await function.InvokeAsync(new AIFunctionArguments(arguments), Ct);
        return invoker.Seen;
    }

    [Fact]
    public async Task JsonElement_values_are_copied_through_unchanged()
    {
        // What the loop actually produces: the model's reply, already parsed.
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"category":"books","count":3,"deep":{"a":[1,2]}}""")!;

        var seen = await RoundTrip(parsed);

        Assert.Contains("\"category\":\"books\"", seen);
        Assert.Contains("\"count\":3", seen);
        Assert.Contains("\"deep\":{\"a\":[1,2]}", seen);
    }

    [Fact]
    public async Task Scalars_assembled_by_hand_are_written_too()
    {
        var seen = await RoundTrip(new Dictionary<string, object?>
        {
            ["text"] = "coffee",
            ["flag"] = true,
            ["whole"] = 7,
            ["fraction"] = 1.5,
            ["money"] = 22.80m,
            ["nothing"] = null,
        });

        Assert.Contains("\"text\":\"coffee\"", seen);
        Assert.Contains("\"flag\":true", seen);
        Assert.Contains("\"whole\":7", seen);
        Assert.Contains("\"money\":22.80", seen);
        Assert.Contains("\"nothing\":null", seen);
    }

    [Fact]
    public async Task A_shape_that_cannot_be_rendered_stops_the_call_by_name()
    {
        // A mangled argument that dispatches is worse than a call that stops,
        // and the generated dispatcher could not have bound this either.
        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => RoundTrip(new Dictionary<string, object?> { ["odd"] = new object() }));

        Assert.Contains("Echo", error.Message);
        Assert.Contains("odd", error.Message);
    }
}
