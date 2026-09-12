using System.Collections.Immutable;
using System.Linq;

using AgentInfer.Generator.Emit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentInfer.Generator;

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
/// A <c>PromptFile</c> adds a fourth step between Transform and Emit, because
/// the text comes from <c>AdditionalTexts</c> rather than from syntax and the
/// two change independently. Resolving it in its own stage means editing a
/// prompt file re-runs the resolve and the emit, and editing a <c>.cs</c> file
/// re-runs the transform — rather than every edit re-running everything.
/// </para>
/// <para>
/// Diagnostics ride alongside the model rather than being reported from
/// <c>Transform</c>, because <c>Transform</c>'s output is cached: report from
/// there and an error disappears the second time a file is analyzed unchanged.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class AgentGenerator : IIncrementalGenerator
{
    private const string AgentAttribute = "AgentInfer.AgentAttribute";
    private const string PromptAttribute = "AgentInfer.PromptAttribute";
    private const string StrategyAttribute = "AgentInfer.StrategyAttribute";
    private const string AgentToolAttribute = "AgentInfer.AgentToolAttribute";
    private const string AgentToolsAttribute = "AgentInfer.AgentToolsAttribute";
    private const string ModelAttribute = "AgentInfer.ModelAttribute";
    private const string AgentInferJsonAttribute = "AgentInfer.AgentInferJsonAttribute";
    private const string JsonSerializableAttribute = "System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string RequiresPermissionAttribute = "AgentInfer.RequiresPermissionAttribute";


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var agents = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AgentAttribute,
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, ct) => Transform(ctx, ct))
            .Where(static result => result is not null);

        // Every AdditionalFile is read, not only the ones some agent names,
        // because which ones are named is not known until the agents and the
        // files are combined. The cost is bounded by Roslyn caching this step
        // per file: it re-reads one file when that file changes, and does
        // nothing at all on a .cs keystroke.
        var promptFiles = context.AdditionalTextsProvider
            .Select(static (text, ct) => new PromptFile(
                text.Path, text.GetText(ct)?.ToString() ?? string.Empty))
            .Collect()
            .Select(static (files, _) => new EquatableArray<PromptFile>(files));

        // Emitted by the SDK into the generated analyzer config. Without it the
        // only way to match a project-relative path against an absolute one is
        // by suffix, which is ambiguous the moment two directories hold a file
        // of the same name — hence AIN010 rather than a guess.
        var projectDirectory = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.projectdir", out var directory)
                    ? directory
                    : string.Empty);

        var resolved = agents
            .Combine(promptFiles.Combine(projectDirectory))
            .Select(static (pair, _) => Resolve(pair.Left!, pair.Right.Left, pair.Right.Right));

        // Every role any agent declared, as one constant, for startup validation.
        //
        // Collect() breaks incrementality for this output — any change re-emits
        // it — which is acceptable for a file of one array and is the only way
        // to see the whole compilation at once.
        context.RegisterSourceOutput(
            agents.Collect(),
            static (spc, results) =>
            {
                var roles = results
                    .Where(r => r?.Model is not null)
                    .SelectMany(r => r!.Model!.Methods)
                    .Select(m => m.ModelRole)
                    .Where(role => !string.IsNullOrWhiteSpace(role))
                    .Select(role => role!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(role => role, StringComparer.Ordinal)
                    .ToArray();

                spc.AddSource("AgentInferRoles.g.cs", Emit.RolesEmitter.Emit(roles));
            });

        // The second way in: [AgentTools] on a type, which emits the invoker and
        // its manifest and nothing else. Its own pipeline rather than a branch
        // of the agent one, because it needs no prompt, no prompt file and no
        // project directory — combining it with those would make a .md
        // keystroke invalidate a tool type that never referenced one.
        var toolTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AgentToolsAttribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, ct) => TransformTools(ctx, ct))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(toolTypes, static (spc, result) =>
        {
            foreach (var diagnostic in result!.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (result.Model is { } tools)
            {
                spc.AddSource($"{tools.InvokerName}.g.cs", Emit.ToolsEmitter.Emit(tools));
            }
        });

        context.RegisterSourceOutput(resolved, static (spc, result) =>
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

    /// <summary>
    /// The assembly's declared <c>JsonSerializerContext</c>, or empty.
    /// </summary>
    /// <remarks>
    /// Read as a string and thrown away with the rest of the symbols. Present
    /// means this assembly has opted into a trimmable typed path; absent keeps
    /// the reflective one, which still works and still says so with
    /// <c>[RequiresUnreferencedCode]</c>.
    /// </remarks>
    private static INamedTypeSymbol? JsonContextOf(GeneratorAttributeSyntaxContext ctx)
    {
        foreach (var attribute in ctx.SemanticModel.Compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != AgentInferJsonAttribute) continue;

            if (attribute.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol context)
            {
                return context;
            }
        }

        return null;
    }

    /// <summary>Whether this type is a <c>JsonSerializerContext</c>.</summary>
    private static bool IsJsonContext(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext") return true;
        }

        return false;
    }

    /// <summary>
    /// Every type the context declares <c>[JsonSerializable]</c> for.
    /// </summary>
    /// <remarks>
    /// Read from the consumer's own source, which is the only part of the other
    /// generator's world this one can see. Its <em>output</em> is invisible;
    /// the attributes that drive it are not.
    /// </remarks>
    private static ImmutableHashSet<string> SerializableTypes(INamedTypeSymbol context)
    {
        var types = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var attribute in context.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != JsonSerializableAttribute) continue;

            if (attribute.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol serialized)
            {
                types.Add(serialized.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        return types.ToImmutable();
    }

    /// <summary>The result of reading one <c>[AgentTools]</c> type.</summary>
    private sealed record ToolsResult(
        ToolsModel? Model,
        EquatableArray<Diagnostic> Diagnostics);

    /// <summary>
    /// Reads a type carrying <c>[AgentTools]</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately thin. Everything interesting is in
    /// <see cref="ReadToolsOf"/>, which the agent path calls too — the whole
    /// point of this entry is that it produces the same tool models from the
    /// same reader, so the two ways in cannot describe one type differently.
    /// </remarks>
    private static ToolsResult? TransformTools(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var tools = ReadToolsOf(type, diagnostics, ct, JsonContextOf(ctx));

        var given = ctx.Attributes.FirstOrDefault()?.NamedArguments
            .FirstOrDefault(pair => pair.Key == "InvokerName").Value.Value as string;

        var invokerName = string.IsNullOrWhiteSpace(given) ? type.Name + "Invoker" : given!.Trim();

        // A type marked [AgentTools] with nothing to dispatch is a mistake
        // worth naming: it generates an invoker that offers a model nothing,
        // which presents as an agent that answers without ever calling a tool.
        if (tools.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.NoToolsOnToolsType, Location(type), type.Name));

            return new ToolsResult(null, new EquatableArray<Diagnostic>(diagnostics.ToImmutable()));
        }

        return new ToolsResult(
            new ToolsModel(
                Namespace: type.ContainingNamespace.IsGlobalNamespace
                    ? string.Empty
                    : type.ContainingNamespace.ToDisplayString(),
                ToolsType: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                InvokerName: invokerName,
                Accessibility: type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                Tools: tools),
            new EquatableArray<Diagnostic>(diagnostics.ToImmutable()));
    }

    /// <summary>The result of reading one interface: a model, some diagnostics, or both.</summary>
    /// <remarks>
    /// A record so it is value-equatable, because it is what the pipeline caches.
    /// <see cref="Diagnostic"/> implements equality by value, so an
    /// <see cref="EquatableArray{T}"/> of them behaves.
    /// </remarks>
    /// <param name="Declaration">
    /// Where to point a diagnostic that cannot be raised until the prompt files
    /// are in hand. Nothing else in a cached model holds a syntax reference;
    /// this one is the same concession <see cref="Diagnostic"/> already makes.
    /// </param>
    private sealed record Result(
        AgentModel? Model,
        EquatableArray<Diagnostic> Diagnostics,
        Location Declaration);

    /// <summary>One <c>AdditionalFiles</c> entry, flattened to strings.</summary>
    private sealed record PromptFile(string Path, string Content) : IEquatable<PromptFile>;

    private static Result? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var attribute = ctx.Attributes.FirstOrDefault();

        var systemPrompt = attribute?.ConstructorArguments
            .FirstOrDefault().Value as string ?? string.Empty;

        var promptFile = (attribute?.NamedArguments
            .FirstOrDefault(pair => pair.Key == "PromptFile").Value.Value as string ?? string.Empty).Trim();

        // A file named alongside an inline prompt is not a merge and not a
        // precedence question — it means one of the two has been edited and the
        // other has not, and nothing here can tell which.
        if (promptFile.Length > 0 && !string.IsNullOrWhiteSpace(systemPrompt))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.ConflictingPromptSources, Location(type), type.Name, promptFile));
            promptFile = string.Empty;
        }

        // Deferred when a file is named: whether that prompt is empty is not
        // knowable until the file has been read, which happens a stage later.
        if (promptFile.Length == 0 && string.IsNullOrWhiteSpace(systemPrompt))
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
        // A property bounded twice would put two values under one schema
        // keyword, which is not a precedence question.
        foreach (var method in methods)
        {
            if (method.Shape != ReturnShape.Json) continue;

            foreach (var conflicted in SchemaWriter.DoublyConstrained(ReturnTypeOf(
                         type.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == method.Name))))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.ConflictingBounds, Location(type), conflicted));
            }
        }

        // The declared JSON context, checked before anything is emitted against
        // it. Both failures below would otherwise surface inside a generated
        // file the user cannot open.
        var jsonContext = string.Empty;

        if (JsonContextOf(ctx) is { } context)
        {
            if (!IsJsonContext(context))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.NotAJsonContext, Location(type), context.ToDisplayString()));
            }
            else
            {
                jsonContext = context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var serializable = SerializableTypes(context);

                foreach (var method in methods)
                {
                    if (method.Shape != ReturnShape.Json) continue;
                    if (serializable.Contains(method.ReturnType)) continue;

                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.ReturnTypeNotSerializable,
                        Location(type),
                        $"{type.Name}.{method.Name}",
                        method.ReturnType,
                        context.Name,
                        method.ReturnType));

                    // Fall back rather than emit a contract that cannot bind.
                    jsonContext = string.Empty;
                    break;
                }
            }
        }

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
            PromptFile: promptFile,
            Methods: new EquatableArray<MethodModel>(methods.ToImmutable()),
            Tools: tools,
            ToolsType: toolsType,
            JsonContext: jsonContext);

        return new Result(
            model,
            new EquatableArray<Diagnostic>(diagnostics.ToImmutable()),
            Location(type));
    }

    /// <summary>
    /// Fills in a prompt that lives in a file, or says why it could not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after Transform and before Emit, so what reaches the emitter is a
    /// model whose prompt is a string either way — the generated file is
    /// identical whichever route the text took, which is the property that
    /// makes this cost nothing at run time.
    /// </para>
    /// <para>
    /// An agent with no <c>PromptFile</c> is returned untouched rather than
    /// rebuilt, so the common case adds one reference comparison.
    /// </para>
    /// </remarks>
    private static Result Resolve(Result result, EquatableArray<PromptFile> files, string projectDirectory)
    {
        if (result.Model is not { PromptFile.Length: > 0 } model) return result;

        var wanted = Normalize(model.PromptFile);
        var matches = ImmutableArray.CreateBuilder<PromptFile>();

        foreach (var file in files)
        {
            if (Matches(file, wanted, projectDirectory)) matches.Add(file);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(result.Diagnostics);

        if (matches.Count == 0)
        {
            // The file is usually sitting right there in the project, so the
            // message carries the line to paste rather than a description of
            // the problem. This is the first thing anyone hits.
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.PromptFileNotFound, result.Declaration, model.InterfaceName, model.PromptFile));

            return result with { Diagnostics = new EquatableArray<Diagnostic>(diagnostics.ToImmutable()) };
        }

        if (matches.Count > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.AmbiguousPromptFile,
                result.Declaration, model.InterfaceName, model.PromptFile, matches.Count));

            return result with { Diagnostics = new EquatableArray<Diagnostic>(diagnostics.ToImmutable()) };
        }

        var content = matches[0].Content;

        // The same rule an inline prompt gets. A file that exists and says
        // nothing is the failure AIN001 is for, arriving by a different road.
        if (string.IsNullOrWhiteSpace(content))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MissingAgentPrompt, result.Declaration, model.InterfaceName));
        }

        return result with
        {
            Model = model with { SystemPrompt = content },
            Diagnostics = new EquatableArray<Diagnostic>(diagnostics.ToImmutable()),
        };
    }

    /// <summary>
    /// Whether one <c>AdditionalFiles</c> entry is the file the agent named.
    /// </summary>
    /// <remarks>
    /// Project-relative when the project directory is known, which is the case
    /// under MSBuild. The suffix fallback covers hosts that do not set it —
    /// a driver in a test, most obviously — and is deliberately allowed to
    /// match more than once so that AIN010 can say so.
    /// </remarks>
    private static bool Matches(PromptFile file, string wanted, string projectDirectory)
    {
        var path = Normalize(file.Path);

        if (string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)) return true;

        if (projectDirectory.Length > 0)
        {
            var root = Normalize(projectDirectory);
            if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(path.Substring(root.Length), wanted, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        return path.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Separators one way round, no leading <c>./</c>.</summary>
    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        return normalized;
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

        return ReadToolsOf(toolsType, diagnostics, ct, JsonContextOf(ctx));
    }

    /// <summary>
    /// Every <c>[AgentTool]</c> method on one type.
    /// </summary>
    /// <remarks>
    /// Shared by both ways in: an agent naming <c>Tools = typeof(X)</c>, and
    /// <c>[AgentTools]</c> on X itself. One reader, because a second one would
    /// start identical and diverge on the first thing either learned — and the
    /// symptom would be a manifest that disagrees with a dispatch switch,
    /// which is the failure this whole file exists to make impossible.
    /// </remarks>
    private static EquatableArray<ToolModel> ReadToolsOf(
        INamedTypeSymbol toolsType,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken ct,
        INamedTypeSymbol? jsonContext = null)
    {
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
                // A flags enum reaches here as "no schema", and the generic
                // message would send the reader to look for a scalar they
                // already have. Name the actual reason.
                if (SchemaWriter.FlagsEnumIn(unsupported!.Type) is { } flags)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.UnsupportedFlagsEnum,
                        Location(unsupported),
                        Display(method),
                        flags.ToDisplayString()));
                    continue;
                }

                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.UnsupportedToolParameter,
                    Location(unsupported),
                    Display(method),
                    unsupported.Type.ToDisplayString(),
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
                    SchemaWriter.ReaderFor(parameter.Type)!,
                    SchemaWriter.DefaultFor(parameter)));
            }

            var shape = ReturnOf(method);
            var renderer = RendererFor(method, shape, jsonContext, diagnostics);

            if (renderer is null) continue;

            tools.Add(new ToolModel(
                Name: method.Name,
                Description: toolAttribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty,
                ParametersSchema: schema,
                Permissions: new EquatableArray<string>(permissions),
                Parameters: new EquatableArray<ToolParameterModel>(parameters.ToImmutable()),
                Return: shape,
                Renderer: renderer,
                TakesCancellationToken: takesToken));
        }

        return new EquatableArray<ToolModel>(tools.ToImmutable());
    }

    /// <summary>
    /// How this tool's result becomes text, or <c>null</c> when it cannot.
    /// </summary>
    /// <remarks>
    /// Three answers, in order of preference. An overload the compiler can pick
    /// from the static type; failing that, a <c>JsonTypeInfo</c> from the
    /// assembly's declared context; failing both, AIN016 — because a reflective
    /// fallback here is how the one remaining reflective call in the tool path
    /// would stay.
    /// </remarks>
    private static string? RendererFor(
        IMethodSymbol method,
        ToolReturn shape,
        INamedTypeSymbol? jsonContext,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (shape is ToolReturn.None or ToolReturn.AwaitedNone)
        {
            return "global::AgentInfer.ToolResult.Done()";
        }

        var returned = ToolResultTypeOf(method);

        if (SchemaWriter.IsRenderable(returned))
        {
            return "global::AgentInfer.ToolResult.Render(result)";
        }

        var name = returned.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (jsonContext is not null && SerializableTypes(jsonContext).Contains(name))
        {
            var context = jsonContext.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            return "global::System.Text.Json.JsonSerializer.Serialize(result, "
                + $"(global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<{name}>)"
                + $"{context}.Default.GetTypeInfo(typeof({name}))!)";
        }

        diagnostics.Add(Diagnostic.Create(
            Diagnostics.UnrenderableToolResult, Location(method), Display(method), name));

        return null;
    }

    /// <summary>
    /// What a tool actually hands back: the <c>T</c> of a <c>Task&lt;T&gt;</c>,
    /// or the return type itself.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ReturnTypeOf"/>, which unwraps <em>any</em> generic
    /// because a generation method always returns <c>Task&lt;T&gt;</c>. A tool
    /// may return <c>IReadOnlyList&lt;Row&gt;</c> directly, and unwrapping that
    /// to <c>Row</c> made AIN016 name a type nobody had written.
    /// </remarks>
    private static ITypeSymbol ToolResultTypeOf(IMethodSymbol method) =>
        method.ReturnType is INamedTypeSymbol { IsGenericType: true } named &&
        named.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.Task<TResult>"
            ? named.TypeArguments[0]
            : method.ReturnType;

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

        // A return type the schema builder cannot describe is not fatal — the
        // JSON path still works, it just tells the model less. A flags enum is
        // the exception, because the reply it invites ("Read, Write") binds to
        // nothing, and finding that out from a trace is what AIN008 exists to
        // prevent.
        if (shape == ReturnShape.Json && SchemaWriter.FlagsEnumIn(ReturnTypeOf(method)) is { } flagsReturn)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.UnsupportedFlagsEnum,
                Location(method),
                Display(method),
                flagsReturn.ToDisplayString()));
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

        var modelRole = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ModelAttribute)
            ?.ConstructorArguments.FirstOrDefault().Value as string;

        if (modelRole is not null && string.IsNullOrWhiteSpace(modelRole))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.EmptyModelRole, Location(method), Display(method)));
            modelRole = null;
        }

        // Reused for the tool loop, not just CodeAct. A method that can call
        // tools can trade turns with them, and that needs a bound wherever the
        // turns come from.
        var maxIterations = strategy?.NamedArguments
            .FirstOrDefault(pair => pair.Key == "MaxIterations").Value.Value as int? ?? 6;

        return new MethodModel(
            Name: method.Name,
            Operation: $"{method.ContainingType.Name}.{method.Name}",
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
                : string.Empty,
            ReturnChecks: new EquatableArray<string>(
                shape == ReturnShape.Json
                    ? [.. SchemaWriter.ReturnChecks(ReturnTypeOf(method))]
                    : ImmutableArray<string>.Empty),
            ModelRole: modelRole);
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
