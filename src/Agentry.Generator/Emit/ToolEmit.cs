using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Agentry.Generator.Emit;

/// <summary>
/// The tool half of what the generator writes, shared by both ways in.
/// </summary>
/// <remarks>
/// An agent naming <c>Tools = typeof(X)</c> and <c>[AgentTools]</c> on X emit
/// the same manifest and the same dispatch switch. Kept in one place rather
/// than written twice, because the failure mode of two copies is a manifest
/// that disagrees with the switch that serves it — a tool the model is offered
/// and cannot call, or worse, one it can call that nothing declared a
/// permission for.
/// </remarks>
internal static class ToolEmit
{
    /// <summary>
    /// The manifest, as a static property built at compile time.
    /// </summary>
    /// <remarks>
    /// A static property rather than something built at construction: the
    /// manifest is a fact about the type, known at compile time, and there is
    /// no moment at run time when it could differ. Building it in a constructor
    /// would be doing work to arrive at a constant.
    /// <para>
    /// Each schema is a verbatim literal. Nothing reflects over the tool method
    /// to produce it, which is what makes this survive trimming.
    /// </para>
    /// </remarks>
    public static void Manifest(StringBuilder code, string indent, IEnumerable<ToolModel> tools)
    {
        code.AppendLine();
        code.Append(indent).AppendLine("/// <summary>What this agent may call. Compile-time constant.</summary>");
        code.Append(indent).AppendLine("public static global::Agentry.ToolManifest Tools { get; } = new(");
        code.Append(indent).AppendLine("    new global::Agentry.ToolDescriptor[]");
        code.Append(indent).AppendLine("    {");

        foreach (var tool in tools)
        {
            code.Append(indent).AppendLine("        new(");
            code.Append(indent).Append("            ").Append(Literal(tool.Name)).AppendLine(",");
            code.Append(indent).Append("            ").Append(Literal(tool.Description)).AppendLine(",");
            code.Append(indent).Append("            ").Append(Literal(tool.ParametersSchema)).AppendLine(",");
            code.Append(indent).Append("            new string[] { ");

            var first = true;
            foreach (var permission in tool.Permissions)
            {
                if (!first) code.Append(", ");
                code.Append(Literal(permission));
                first = false;
            }

            code.AppendLine(" }),");
        }

        code.Append(indent).AppendLine("    });");
    }

    /// <summary>The invoker class: a switch on the name, with typed binding.</summary>
    public static void Invoker(
        StringBuilder code,
        string accessibility,
        string invokerName,
        string toolsType,
        string manifestExpression,
        IEnumerable<ToolModel> tools)
    {
        code.AppendLine();
        code.AppendLine("/// <summary>Dispatches these tools. Generated.</summary>");
        code.Append(accessibility).Append(" sealed partial class ").Append(invokerName)
            .AppendLine(" : global::Agentry.ToolInvoker");
        code.AppendLine("{");
        code.Append("    private readonly ").Append(toolsType).AppendLine(" _tools;");
        code.AppendLine();
        code.Append("    public ").Append(invokerName).Append('(').Append(toolsType)
            .AppendLine(" tools) => _tools = tools;");
        code.AppendLine();
        code.Append("    public override global::Agentry.ToolManifest Manifest => ")
            .Append(manifestExpression).AppendLine(";");
        code.AppendLine();
        code.AppendLine("    /// <inheritdoc/>");
        code.AppendLine("    protected override async global::System.Threading.Tasks.Task<string> DispatchAsync(");
        code.AppendLine("        string name,");
        code.AppendLine("        global::System.Text.Json.JsonElement arguments,");
        code.AppendLine("        global::System.Threading.CancellationToken ct)");
        code.AppendLine("    {");
        code.AppendLine("        switch (name)");
        code.AppendLine("        {");

        foreach (var tool in tools)
        {
            Case(code, tool);
        }

        code.AppendLine("            default:");
        code.AppendLine("                // Unreachable: the base class matches the name against the");
        code.AppendLine("                // manifest first. Here so a tool added to one and not the");
        code.AppendLine("                // other fails loudly instead of returning null.");
        code.AppendLine("                throw new global::System.InvalidOperationException($\"No dispatch for \'{name}\'.\");");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("}");
    }

    private static void Case(StringBuilder code, ToolModel tool)
    {
        code.Append("            case ").Append(Literal(tool.Name)).AppendLine(":");
        code.AppendLine("            {");

        foreach (var parameter in tool.Parameters)
        {
            code.Append("                var ").Append(parameter.Name).Append(" = arguments.GetProperty(")
                .Append(Literal(parameter.Name)).Append(").").Append(parameter.Reader).AppendLine(";");
        }

        var args = string.Join(", ", tool.Parameters.Select(p => p.Name)
            .Concat(tool.TakesCancellationToken ? ["ct"] : System.Array.Empty<string>()));

        var call = $"_tools.{tool.Name}({args})";

        switch (tool.Return)
        {
            case ToolReturn.None:
                code.Append("                ").Append(call).AppendLine(";");
                code.AppendLine("                await global::System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);");
                code.AppendLine("                return \"done\";");
                break;

            case ToolReturn.AwaitedNone:
                code.Append("                await ").Append(call).AppendLine(".ConfigureAwait(false);");
                code.AppendLine("                return \"done\";");
                break;

            case ToolReturn.AwaitedValue:
                code.Append("                var result = await ").Append(call).AppendLine(".ConfigureAwait(false);");
                code.AppendLine("                return global::System.Text.Json.JsonSerializer.Serialize(result);");
                break;

            default:
                code.Append("                var result = ").Append(call).AppendLine(";");
                code.AppendLine("                await global::System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);");
                code.AppendLine("                return global::System.Text.Json.JsonSerializer.Serialize(result);");
                break;
        }

        code.AppendLine("            }");
    }

    /// <summary>
    /// A verbatim string literal, with quotes doubled.
    /// </summary>
    /// <remarks>
    /// Prompts are multi-line and full of punctuation, so verbatim is the only
    /// sane form. The one escape a verbatim literal needs is <c>""</c>, and
    /// getting this wrong produces generated code that does not compile — with
    /// the error reported against a file the user cannot open.
    /// </remarks>
    public static string Literal(string value) => "@\"" + value.Replace("\"", "\"\"") + "\"";
}
