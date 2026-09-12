using System.Text.Json;

using Xunit;

namespace AgentInfer.Tests;

/// <summary>
/// The gating the generated dispatcher sits behind.
/// </summary>
/// <remarks>
/// Access is checked in two places and these cover both. Filtering the menu
/// stops the model asking; checking at dispatch stops a conversation that began
/// before a permission changed and still has the old tool in its context.
/// </remarks>
public sealed class ToolInvokerTests
{
    /// <summary>Stands in for the generated class. Same base, same behavior.</summary>
    private sealed class Fake : ToolInvoker
    {
        public string? Dispatched { get; private set; }

        public override ToolManifest Manifest { get; } = new(
        [
            new("Read", "Reads.", """{"type":"object","properties":{}}""", ["ledger.read"]),
            new("Write", "Writes.", """{"type":"object","properties":{}}""", ["ledger.write"]),
            new("Both", "Needs two.", """{"type":"object","properties":{}}""", ["ledger.read", "ledger.write"]),
            new("Open", "Needs nothing.", """{"type":"object","properties":{}}""", []),
        ]);

        protected override Task<string> DispatchAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Dispatched = name;
            return Task.FromResult("ok");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_menu_only_offers_what_the_caller_may_use()
    {
        var available = new Fake().AvailableTo(new GrantedPermissions(["ledger.read"]));

        // The model is never told about the rest, so it cannot decide to want them.
        Assert.Equal(["Read", "Open"], available.Select(t => t.Name));
    }

    [Fact]
    public void Every_declared_permission_is_required_not_any()
    {
        var available = new Fake().AvailableTo(new GrantedPermissions(["ledger.read", "ledger.write"]));
        Assert.Contains(available, t => t.Name == "Both");

        var partial = new Fake().AvailableTo(new GrantedPermissions(["ledger.read"]));
        // "Requires" cannot surprise anybody in the direction that matters.
        Assert.DoesNotContain(partial, t => t.Name == "Both");
    }

    [Fact]
    public void A_tool_with_no_permissions_is_open_to_everyone()
    {
        var available = new Fake().AvailableTo(new GrantedPermissions([]));
        Assert.Equal(["Open"], available.Select(t => t.Name));
    }

    [Fact]
    public async Task Dispatch_checks_again_even_though_the_menu_already_filtered()
    {
        var invoker = new Fake();

        // The second enforcement point: a conversation that began before a
        // permission was revoked still has the tool written down in its context.
        var error = await Assert.ThrowsAsync<ToolDeniedException>(
            () => invoker.InvokeAsync("Write", "{}", new GrantedPermissions(["ledger.read"]), Ct));

        Assert.Equal("Write", error.Tool);
        Assert.Equal("ledger.write", error.Permission);
        Assert.Null(invoker.Dispatched);
    }

    [Fact]
    public async Task A_permitted_tool_reaches_dispatch()
    {
        var invoker = new Fake();
        var result = await invoker.InvokeAsync("Read", "{}", new GrantedPermissions(["ledger.read"]), Ct);

        Assert.Equal("ok", result);
        Assert.Equal("Read", invoker.Dispatched);
    }

    [Fact]
    public async Task An_unknown_tool_is_refused_rather_than_dispatched()
    {
        var invoker = new Fake();

        await Assert.ThrowsAsync<ToolDeniedException>(
            () => invoker.InvokeAsync("DropDatabase", "{}", GrantAllTools.Instance, Ct));

        Assert.Null(invoker.Dispatched);
    }

    [Fact]
    public async Task Empty_arguments_are_treated_as_an_empty_object()
    {
        var invoker = new Fake();
        // Models send "" and omit the field entirely; neither should be a parse error.
        await invoker.InvokeAsync("Open", "", new GrantedPermissions([]), Ct);
        Assert.Equal("Open", invoker.Dispatched);
    }

    [Fact]
    public void GrantAll_is_named_for_what_it_does()
    {
        // It appears in registration code and somebody should notice it there.
        Assert.True(GrantAllTools.Instance.IsGranted("anything.at.all"));
    }
}
