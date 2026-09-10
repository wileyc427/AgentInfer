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
            Methods: new EquatableArray<MethodModel>(methods.ToImmutable()));

        return new Result(model, new EquatableArray<Diagnostic>(diagnostics.ToImmutable()));
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

        return new MethodModel(
            Name: method.Name,
            TaskPrompt: promptAttribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty,
            Shape: shape,
            ReturnType: returnType,
            Parameters: new EquatableArray<ParameterModel>(parameters.ToImmutable()),
            CancellationTokenParameter: cancellationToken);
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
