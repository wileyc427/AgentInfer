using Xunit;

namespace AgentInfer.Tests;

public enum Window { Day, NextWeek }

/// <summary>
/// A tool's result on its way back to the model.
/// </summary>
/// <remarks>
/// It used to go through <c>JsonSerializer.Serialize&lt;T&gt;</c>, which picks
/// a converter from the run-time type — one line below the typed argument
/// binding this library is built on. Overloads put the choice back where the
/// rest of the tool surface makes it: at compile time.
/// </remarks>
public sealed class ToolResultTests
{
    [Fact]
    public void A_string_comes_back_as_JSON_rather_than_raw()
    {
        // The model reads a JSON value, so a string is quoted and escaped by
        // the platform rather than by us.
        Assert.Equal("\"coffee\"", ToolResult.Render("coffee"));
        Assert.Equal("\"say \\u0022hi\\u0022\"", ToolResult.Render("say \"hi\""));
    }

    [Fact]
    public void A_decimal_keeps_its_form()
    {
        // Written as decimal literals, not [InlineData] doubles: an attribute
        // argument cannot carry a decimal, so the trailing zero would be lost
        // before Render ever saw it.
        Assert.Equal("22.80", ToolResult.Render(22.80m));
        Assert.Equal("0.5", ToolResult.Render(0.5m));
    }

    [Fact]
    public void Scalars_render_without_a_serializer()
    {
        Assert.Equal("true", ToolResult.Render(true));
        Assert.Equal("7", ToolResult.Render(7));
        Assert.Equal("1.5", ToolResult.Render(1.5));
    }

    [Fact]
    public void An_enum_comes_back_under_its_wire_name()
    {
        // camelCased to match what the generator writes into the schema, so a
        // model reading a result and a model reading a schema see one spelling.
        Assert.Equal("\"nextWeek\"", ToolResult.Render(Window.NextWeek));
        Assert.Equal("[\"day\",\"nextWeek\"]", ToolResult.Render(new[] { Window.Day, Window.NextWeek }));
    }

    [Fact]
    public void Arrays_render_as_arrays()
    {
        Assert.Equal("[\"a\",\"b\"]", ToolResult.Render(new[] { "a", "b" }));
        Assert.Equal("[1,2,3]", ToolResult.Render(new[] { 1, 2, 3 }));
        Assert.Equal("[]", ToolResult.Render(System.Array.Empty<string>()));
    }

    [Fact]
    public void A_tool_that_returns_nothing_says_so()
    {
        // Not an empty string: a tool that ran and returned nothing is not the
        // same as one that returned nothing because it failed.
        Assert.Equal("done", ToolResult.Done());
    }
}
