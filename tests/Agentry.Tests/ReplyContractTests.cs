using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Xunit;

namespace Agentry.Tests;

public sealed record Score(int Value, string Reason);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Score))]
internal partial class TestJson : JsonSerializerContext;

/// <summary>A contract that keeps its schema and its rule two lines apart.</summary>
internal sealed class ScoreContract : IReplyContract<Score>
{
    public static ScoreContract Instance { get; } = new();

    public string Schema =>
        """{"type":"object","properties":{"value":{"type":"integer","minimum":1,"maximum":5},"reason":{"type":"string"}},"required":["value","reason"],"additionalProperties":false}""";

    public JsonTypeInfo<Score> TypeInfo => TestJson.Default.Score;

    public string? Validate(Score value) =>
        value.Value is < 1 or > 5 ? $"value must be between 1 and 5, not {value.Value}" : null;
}

/// <summary>
/// The typed path through a contract: no reflection, and the three derived
/// things travel together.
/// </summary>
public sealed class ReplyContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCall Call() => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = "Score it.",
        Operation = "IThing.ScoreAsync",
        Arguments = [],
    };

    [Fact]
    public async Task A_reply_binds_through_the_contract()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"value":4,"reason":"mostly right"}"""));

        var score = await runner.CompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        Assert.Equal(4, score.Value);
        Assert.Equal("mostly right", score.Reason);
    }

    [Fact]
    public async Task The_contract_supplies_the_schema_so_the_call_cannot_disagree()
    {
        var client = new FakeChatClient("""{"value":4,"reason":"ok"}""");

        // Nothing sets ResponseSchema on the call. The contract owns it, which
        // is the point: there is no second place for it to be wrong.
        await new AgentRunner(client).CompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        Assert.Contains("\"maximum\":5", client.Received![1].Text);
    }

    [Fact]
    public async Task A_value_outside_the_declared_bound_is_refused()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"value":100,"reason":"great"}"""));

        var error = await Assert.ThrowsAsync<AgentException>(
            () => runner.CompleteJsonAsync(Call(), ScoreContract.Instance, Ct));

        // 100 is a perfectly good integer, which is why the schema alone was
        // never enough. The message names the rule and quotes the reply.
        Assert.Contains("between 1 and 5", error.Message);
        Assert.Contains("100", error.Message);
    }

    [Fact]
    public async Task A_reply_that_will_not_bind_still_says_what_the_model_said()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"value":4}"""));

        var error = await Assert.ThrowsAsync<AgentException>(
            () => runner.CompleteJsonAsync(Call(), ScoreContract.Instance, Ct));

        Assert.Contains("reason", error.Message);
    }
}
