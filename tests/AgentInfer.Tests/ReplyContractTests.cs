using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Xunit;

namespace AgentInfer.Tests;

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
    public async Task An_anticipated_failure_is_reported_rather_than_thrown()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"value":100,"reason":"great"}"""));

        var attempt = await runner.TryCompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        // A model answering out of range has not malfunctioned — it has done
        // something the caller anticipates and handles. Throwing made every
        // ordinary run report a first-chance exception in a debugger.
        Assert.False(attempt.Succeeded);
        Assert.Contains("between 1 and 5", attempt.Problem);
        Assert.Contains("100", attempt.Problem);
    }

    [Fact]
    public async Task A_good_reply_comes_back_as_a_value()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"value":4,"reason":"ok"}"""));

        var (succeeded, score, problem) = await runner.TryCompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        Assert.True(succeeded);
        Assert.Null(problem);
        Assert.Equal(4, score.Value);
    }

    [Fact]
    public async Task The_problem_is_the_sentence_the_exception_would_have_carried()
    {
        var reply = """{"value":100,"reason":"great"}""";

        var attempt = await new AgentRunner(new FakeChatClient(reply))
            .TryCompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        var thrown = await Assert.ThrowsAsync<AgentException>(
            () => new AgentRunner(new FakeChatClient(reply))
                .CompleteJsonAsync(Call(), ScoreContract.Instance, Ct));

        // One binding path, so the two cannot disagree about what counts as a
        // usable reply or about how an unusable one is described — and the
        // repair can hand back exactly what the exception would have said.
        Assert.EndsWith(attempt.Problem, thrown.Message);
    }

    [Fact]
    public async Task Reading_a_failed_attempt_says_what_went_wrong()
    {
        var attempt = await new AgentRunner(new FakeChatClient("""{"value":100,"reason":"x"}"""))
            .TryCompleteJsonAsync(Call(), ScoreContract.Instance, Ct);

        var error = Assert.Throws<InvalidOperationException>(() => attempt.Value);
        Assert.Contains("between 1 and 5", error.Message);
    }

    [Fact]
    public async Task A_transport_failure_still_throws()
    {
        // Not an outcome the model produced, so nothing is gained by making
        // every caller check for it.
        var runner = new AgentRunner(new ThrowingClient());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => runner.TryCompleteJsonAsync(Call(), ScoreContract.Instance, Ct));
    }

    private sealed class ThrowingClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task A_schema_on_the_call_and_a_contract_is_refused()
    {
        var runner = new AgentRunner(new FakeChatClient("""{"value":4,"reason":"ok"}"""));
        var call = Call() with { ResponseSchema = """{"type":"object"}""" };

        // Not a precedence question: one of the two has been edited and the
        // other has not, and nothing here can tell which.
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => runner.CompleteJsonAsync(call, ScoreContract.Instance, Ct));

        Assert.Contains("contract owns the schema", error.Message);
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
