using Microsoft.Extensions.AI;

using Xunit;

namespace AgentInfer.Tests;

public enum Urgency { Low, Normal, NeedsAttention }

/// <summary>
/// Binding the other half of the routing story.
/// </summary>
/// <remarks>
/// The schema tells the model which words are legal; these say what happens
/// when it uses one. <c>JsonSerializerOptions.Web</c> carries no string-enum
/// converter, so before this the correct reply <c>"high"</c> failed to bind
/// with a message about a value that could not be converted — for a method
/// whose whole purpose was to name one of three words.
/// </remarks>
public sealed class EnumBindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCall Call(string schema = "") => new()
    {
        SystemPrompt = "You are terse.",
        TaskPrompt = "How urgent?",
        Operation = "ITriage.ClassifyAsync",
        Arguments = [],
        ResponseSchema = schema,
    };

    [Theory]
    [InlineData("\"needsAttention\"")]
    [InlineData("needsAttention")]              // unquoted: the word is the answer
    [InlineData("\"NeedsAttention\"")]          // the converter matches case-insensitively
    [InlineData("```json\n\"needsAttention\"\n```")]
    public async Task An_enum_reply_binds(string reply)
    {
        var runner = new AgentRunner(new FakeChatClient(reply));
        Assert.Equal(Urgency.NeedsAttention, await runner.CompleteJsonReflectivelyAsync<Urgency>(Call(), ct: Ct));
    }

    [Fact]
    public async Task A_word_that_is_not_a_member_fails_at_the_boundary()
    {
        var runner = new AgentRunner(new FakeChatClient("\"urgent\""));

        var error = await Assert.ThrowsAsync<AgentException>(
            () => runner.CompleteJsonReflectivelyAsync<Urgency>(Call(), ct: Ct));

        // The reply is in the message, because that is the thing worth seeing.
        Assert.Contains("urgent", error.Message);
    }

    [Fact]
    public async Task Prose_is_not_quietly_turned_into_a_json_string()
    {
        var runner = new AgentRunner(new FakeChatClient("this request seems urgent to me"));

        // Quoted() is narrow on purpose: a sentence is not a bare token, so it
        // stays invalid JSON and fails rather than binding to something.
        await Assert.ThrowsAsync<AgentException>(
            () => runner.CompleteJsonReflectivelyAsync<Urgency>(Call(), ct: Ct));
    }

    [Fact]
    public async Task A_scalar_schema_is_not_sent_as_a_response_format()
    {
        var client = new FakeChatClient("\"low\"");
        var schema = "{\"type\":\"string\",\"enum\":[\"low\",\"normal\",\"needsAttention\"]}";

        await new AgentRunner(client).CompleteJsonReflectivelyAsync<Urgency>(Call(schema), ct: Ct);

        // Structured output wants an object at the root and rejects this one,
        // so sending it turns a call that would have worked into a 400.
        Assert.Null(client.LastOptions?.ResponseFormat);

        // The prompt still carries it, which is the half that was working.
        Assert.Contains("\"enum\":[\"low\"", client.Received![1].Text);
    }

    [Fact]
    public async Task An_object_schema_is_still_sent_as_a_response_format()
    {
        var client = new FakeChatClient("{\"urgency\":\"low\",\"reason\":\"routine\"}");
        var schema = "{\"type\":\"object\",\"properties\":{\"urgency\":{\"type\":\"string\"}}}";

        await new AgentRunner(client).CompleteJsonReflectivelyAsync<Triage>(Call(schema), ct: Ct);

        Assert.NotNull(client.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task An_enum_inside_a_record_binds()
    {
        var runner = new AgentRunner(new FakeChatClient("{\"urgency\":\"needsAttention\",\"reason\":\"disk full\"}"));

        var triage = await runner.CompleteJsonReflectivelyAsync<Triage>(Call(), ct: Ct);

        Assert.Equal(Urgency.NeedsAttention, triage.Urgency);
        Assert.Equal("disk full", triage.Reason);
    }

    public sealed record Triage(Urgency Urgency, string Reason);
}
