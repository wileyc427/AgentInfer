using System.Collections.Immutable;
using System.Linq;

using Agentry.Generator.Emit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Agentry.Generator;

/// <summary>
/// Turns an <c>[Agent]</c> interface into an implementation, at build time.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is three stages and the order matters:
/// </para>
/// <list type="number">
///   <item><b>Find</b> — <c>ForAttributeWithMetadataName</c> asks Roslyn for
///   nodes carrying one attribute. Roslyn keeps an index for this, so it is
///   effectively free. The older <c>CreateSyntaxProvider</c> runs a predicate
///   over every node in every file on every keystroke; do not use it when an
///   attribute identifies the target.</item>
///   <item><b>Transform</b> — read symbols, build a value-equatable
///   <see cref="AgentModel"/>, discard the symbols. See the note on
///   <see cref="AgentModel"/> for why that discarding is not optional.</item>
///   <item><b>Emit</b> — pure string building from the model. No compilation
///   access, so it cannot accidentally re-root anything.</item>
/// </list>
/// <para>
/// Diagnostics ride alongside the model rather than being reported from
/// <c>Transform</c>, because <c>Transform</c>'s output is cached: report from
/// there and an error disappears the second time a file is analysed unchanged.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class AgentGenerator : IIncrementalGenerator
{
    private const string AgentAttribute = "Agentry.AgentAttribute";
    private const string PromptAttribute = "Agentry.PromptAttribute";
    private const string StrategyAttribute = "Agentry.StrategyAttribute";
    private const string AgentToolAttribute = "Agentry.AgentToolAttribute";
    private const string RequiresPermissionAttribute = "Agentry.RequiresPermissionAttribute";


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var agents = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AgentAttribute,
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, ct) => Transform(ctx, ct))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(agents, static (spc, result) =>
        {
            foreach (var diagnostic in result!.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (result.Model is { } model)
            {
                spc.AddSource($"{model.ImplementationName}.g.cs", AgentEmitter.Emit(model));
            }
        });
    }

    /// <summary>The result of reading one interface: a model, some diagnostics, or both.</summary>
    /// <remarks>
    /// A record so it is value-equatable, because it is what the pipeline caches.
    /// <see cref="Diagnostic"/> implements equality by value, so an
    /// <see cref="EquatableArray{T}"/> of them behaves.
    /// </remarks>
    private sealed record Result(AgentModel? Model, EquatableArray<Diagnostic> Diagnostics);

    private static Result? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var systemPrompt = ctx.Attributes
            .FirstOrDefault()?.ConstructorArguments
            .FirstOrDefault().Value as string ?? string.Empty;

        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MissingAgentPrompt, Location(type), type.Name));
        }

        var methods = ImmutableArray.CreateBuilder<MethodModel>();

        foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            var method = ReadMethod(member, diagnostics);
            if (method is not null) methods.Add(method);
        }

        var tools = ReadTools(ctx, diagnostics, ct, out var toolsType);
        var implementationName = ImplementationNameFor(type, ctx);

        // A model is still produced alongside errors so the IDE keeps offering
        // completion for the members that ARE valid. Half a generated file beats
        // a red squiggle on every use site while you fix one attribute.
        var model = new AgentModel(
            Namespace: type.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : type.ContainingNamespace.ToDisplayString(),
            InterfaceName: type.Name,
            ImplementationName: implementationName,
            Accessibility: type.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public ? "public" : "internal",
            SystemPrompt: systemPrompt,
            Methods: new EquatableArray<MethodModel>(methods.ToImmutable()),
            Tools: tools,
            ToolsType: toolsType);

        return new Result(model, new EquatableArray<Diagnostic>(diagnostics.ToImmutable()));
    }

    /// <summary>
    /// Reads the <c>[AgentTool]</c> methods on whatever <c>[Agent(Tools = ...)]</c> names.
    /// </summary>
    /// <remarks>
    /// Named on the agent rather than discovered assembly-wide, so "what may
    /// this agent reach" is answerable by reading one line rather than by
    /// grepping. The schema for each is built here, while the symbols are in
    /// hand, and travels onward as text.
    /// </remarks>
    private static EquatableArray<ToolModel> ReadTools(
        GeneratorAttributeSyntaxContext ctx,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken ct,
        out string toolsTypeName)
    {
        toolsTypeName = string.Empty;

        var toolsType = ctx.Attributes.FirstOrDefault()?.NamedArguments
            .FirstOrDefault(pair => pair.Key == "Tools").Value.Value as INamedTypeSymbol;

        if (toolsType is null) return new EquatableArray<ToolModel>(ImmutableArray<ToolModel>.Empty);

        toolsTypeName = toolsType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var tools = ImmutableArray.CreateBuilder<ToolModel>();

        foreach (var method in toolsType.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();

            var toolAttribute = method.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AgentToolAttribute);

            // Opt-in, one method at a time. Nothing is exposed to a model
            // because it happens to be public: a public method is a contract
            // with other code, which is a different proposition from a menu
            // item handed to something trying to be helpful.
            if (toolAttribute is null) continue;

            var schema = SchemaWriter.TryWrite(method, out var unsupported);
            if (schema is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.UnsupportedToolParameter,
                    Location(unsupported!),
                    Display(method),
                    unsupported!.Type.ToDisplayString(),
                    unsupported.Name));
                continue;
            }

            var permissions = method.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == RequiresPermissionAttribute)
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty)
                .Where(p => p.Length > 0)
                .ToImmutableArray();

            if (permissions.IsEmpty)
            {
                // A warning rather than an error: a tool with no permission is a
                // decision somebody may legitimately make, and it should be one
                // they made rather than one they defaulted into.
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.MissingToolPermission, Location(method), Display(method)));
            }

            var parameters = ImmutableArray.CreateBuilder<ToolParameterModel>();
            var takesToken = false;

            foreach (var parameter in method.Parameters)
            {
                if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
                {
                    takesToken = true;
                    continue;
                }

                parameters.Add(new ToolParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    SchemaWriter.ReaderFor(parameter.Type)!));
            }

            tools.Add(new ToolModel(
                Name: method.Name,
                Description: toolAttribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty,
                ParametersSchema: schema,
                Permissions: new EquatableArray<string>(permissions),
                Parameters: new EquatableArray<ToolParameterModel>(parameters.ToImmutable()),
                Return: ReturnOf(method),
                TakesCancellationToken: takesToken));
        }

        return new EquatableArray<ToolModel>(tools.ToImmutable());
    }

    /// <summary>The T of a Task&lt;T&gt;, for describing what the model must produce.</summary>
    private static ITypeSymbol ReturnTypeOf(IMethodSymbol method) =>
        method.ReturnType is INamedTypeSymbol { IsGenericType: true } named
            ? named.TypeArguments[0]
            : method.ReturnType;

    /// <summary>How the invoker has to treat this tool's result.</summary>
    private static ToolReturn ReturnOf(IMethodSymbol method)
    {
        var type = method.ReturnType;

        if (type.SpecialType == SpecialType.System_Void) return ToolReturn.None;

        if (type is INamedTypeSymbol named && named.ToDisplayString().StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
        {
            return named.IsGenericType ? ToolReturn.AwaitedValue : ToolReturn.AwaitedNone;
        }

        return ToolReturn.Value;
    }

    private static MethodModel? ReadMethod(IMethodSymbol method, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var promptAttribute = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == PromptAttribute);

        if (promptAttribute is null)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MissingMethodPrompt, Location(method), Display(method)));
            return null;
        }

        var strategy = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == StrategyAttribute);

        // Predict is the absence of the attribute. Asking for CodeAct is an
        // error today rather than a silent downgrade to Predict — a method that
        // quietly did something other than what it says would be exactly the
        // class of surprise this library exists to remove.
        if (strategy?.ConstructorArguments.FirstOrDefault().Value is int value && value == 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.CodeActNotAvailable, Location(method), Display(method)));
            return null;
        }

        if (!TryReadReturn(method.ReturnType, out var shape, out var returnType))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.UnsupportedReturnType,
                Location(method),
                Display(method),
                method.ReturnType.ToDisplayString()));
            return null;
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterModel>();
        string? cancellationToken = null;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                cancellationToken = parameter.Name;
                continue;
            }

            // FullyQualifiedFormat, not ToDisplayString(). It yields
            // "global::System.String" rather than "string", which matters
            // because a generator cannot safely prepend "global::" itself: a
            // keyword alias like `string` rejects the prefix, and the emitted
            // `global::string` binds to nothing. The resulting error is
            // CS0535 "does not implement interface member" — pointing at the
            // class, not at the bad parameter — which is a long way from the
            // cause.
            var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            // SpecialType, never a string comparison. FullyQualifiedFormat keeps
            // the `string` keyword alias rather than expanding it to
            // System.String, so matching on the rendered name silently fails and
            // every string argument gets JSON-encoded into its own prompt.
            // Caught by reading the generated file, which is why the sample
            // emits it and why there is a snapshot test.
            var isString = parameter.Type.SpecialType == SpecialType.System_String;
            parameters.Add(new ParameterModel(parameter.Name, type, isString));
        }

        // Reused for the tool loop, not just CodeAct. A method that can call
        // tools can trade turns with them, and that needs a bound wherever the
        // turns come from.
        var maxIterations = strategy?.NamedArguments
            .FirstOrDefault(pair => pair.Key == "MaxIterations").Value.Value as int? ?? 6;

        return new MethodModel(
            Name: method.Name,
            TaskPrompt: promptAttribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty,
            Shape: shape,
            ReturnType: returnType,
            Parameters: new EquatableArray<ParameterModel>(parameters.ToImmutable()),
            CancellationTokenParameter: cancellationToken,
            MaxIterations: maxIterations,
            // Empty for a text return: a string needs no shape, and telling a
            // model to reply with {"type":"string"} is a way to get a JSON
            // document containing prose.
            ReturnSchema: shape == ReturnShape.Json
                ? SchemaWriter.TryWriteReturn(ReturnTypeOf(method)) ?? string.Empty
                : string.Empty);
    }

    private static bool TryReadReturn(ITypeSymbol returnType, out ReturnShape shape, out string type)
    {
        shape = ReturnShape.Text;
        type = "string";

        if (returnType is not INamedTypeSymbol { IsGenericType: true } named) return false;
        if (named.ConstructedFrom.ToDisplayString() != "System.Threading.Tasks.Task<TResult>") return false;

        var argument = named.TypeArguments[0];
        type = argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        shape = argument.SpecialType == SpecialType.System_String ? ReturnShape.Text : ReturnShape.Json;
        return true;
    }

    private static string ImplementationNameFor(INamedTypeSymbol type, GeneratorAttributeSyntaxContext ctx)
    {
        var named = ctx.Attributes.FirstOrDefault()?.NamedArguments
            .FirstOrDefault(pair => pair.Key == "ImplementationName").Value.Value as string;

        if (!string.IsNullOrWhiteSpace(named)) return named!;

        var name = type.Name;
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])) name = name.Substring(1);
        return name + "Agent";
    }

    private static Location Location(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault() ?? Microsoft.CodeAnalysis.Location.None;

    private static string Display(IMethodSymbol method) =>
        $"{method.ContainingType.Name}.{method.Name}";
}
