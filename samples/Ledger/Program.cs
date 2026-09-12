using System.ClientModel;
using System.Net.Sockets;

using AgentInfer;

using Ledger;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Configuration, with the precedence deliberately inverted.
//
// The .NET convention is JSON first and environment variables last, so the
// environment wins. These samples do the opposite: a value written in
// appsettings beats one in the environment. That is not how a production host
// should be wired, and it is the right call here — you edit a file, run, and
// what you typed is what runs, rather than losing to an export from an hour ago
// that nothing on screen mentions.
//
// "Beats" means a value that is PRESENT wins. A key absent from the file still
// falls through to the environment, which is what keeps a committed file from
// blanking a real credential.
//
// appsettings.Development.json is last and is gitignored: it is where a local
// key goes.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddEnvironmentVariables()
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var models = new Models(configuration);

using var logs = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options => options.SingleLine = true));

// Nothing listens to a Meter by default, so every instrument in this library
// records into a void. --metrics prints the distributions at exit; a real host
// points OpenTelemetry at the "AgentInfer" meter instead.
var metrics = args.Contains("--metrics", StringComparer.Ordinal) ? new Meters() : null;

// AddAgentInferModels registers one keyed AgentRunner per configured role and an
// IModelRouter over them. ValidateRoles then fails HERE — at startup — if any
// role the code asks for has no model configured, rather than on the first call
// that needs it. AgentInferRoles.All is generated from the [Model] attributes, so
// the check is against the roles actually used.
var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(logs);
services.AddLogging();

var agentInfer = services
    .AddAgentInferModels(configuration, (binding, _) => models.For(binding))
    .ValidateRoles(AgentInferRoles.All);

var provider = services.BuildServiceProvider();
var router = provider.GetRequiredService<IModelRouter>();

// Methods with no [Model] use this one.
var runner = new AgentRunner(models.Default(), logs.CreateLogger<AgentRunner>());

// Which model each method will actually reach, and from where. A run that does
// not say this is a run you cannot argue with when the answer looks wrong.
Console.WriteLine($"default:  {Configured(configuration, "AgentInfer:DefaultModel")} at {models.EndpointOf(models.DefaultProvider)}");
foreach (var (role, binding) in agentInfer.Models)
{
    Console.WriteLine(
        $"role {role}: {binding.Model} at {binding.Provider.Endpoint}  (key: {binding.Provider.KeySource})");
}

// The caller may read the ledger and not write to it. LedgerTools declares a
// Reclassify tool requiring "ledger.write", so the model is never told it
// exists — and would be refused if it asked anyway.
var caller = new GrantedPermissions(["ledger.read"]);
var invoker = new LedgerAnalystAgentTools(new LedgerTools());

Console.WriteLine(
    $"tools: {string.Join(", ", invoker.AvailableTo(caller).Select(t => t.Name))} " +
    $"(of {invoker.Manifest.Tools.Count}; the rest need permissions this caller lacks)\n");

// Generated. In a real host all four come from DI.
ILedgerAnalyst analyst = new LedgerAnalystAgent(runner, invoker, caller, router);

try
{
    var summary = await analyst.SummarizeAsync();
    Console.WriteLine($"summary: {summary}\n");

    var verdict = await analyst.ReviewAsync(summary);
    Console.WriteLine($"verdict: approved={verdict.Approved} score={verdict.Score}/5");
    foreach (var problem in verdict.Problems)
    {
        Console.WriteLine($"  · {problem}");
    }
}
catch (AgentException error)
{
    // The model answered but the reply would not bind, or failed validation.
    // The message carries what it actually said, which is the thing worth
    // seeing.
    Console.Error.WriteLine($"\n{error.Message}");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"\n{Explain(error)}");
    return 1;
}

metrics?.Report();
metrics?.Dispose();

return 0;

/// <summary>
/// Turns a transport failure into one readable line.
/// </summary>
/// <remarks>
/// A refused connection arrives as an AggregateException("Retry failed after 4
/// tries") wrapping a ClientResultException wrapping an HttpRequestException
/// wrapping a SocketException, and the default output is forty lines of
/// pipeline-policy stack. The cause is in there; nothing surfaces it.
/// </remarks>
static string Explain(Exception error)
{
    var cause = error is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions[0]
        : error;

    while (cause.InnerException is { } inner) cause = inner;

    return cause switch
    {
        SocketException or HttpRequestException =>
            $"Could not reach the endpoint ({cause.Message}).\n" +
            "Start it with `ollama serve`, or change AgentInfer:Endpoint in appsettings.json.",

        ClientResultException { Status: 401 or 403 } =>
            "The endpoint refused the credential. Set OPENAI_API_KEY, or clear it for a local model.",

        ClientResultException { Status: 404 } =>
            "The endpoint answered but does not have that model. Check `ollama list` and "
            + "AgentInfer:DefaultModel in appsettings.json.",

        _ => $"{cause.GetType().Name}: {cause.Message}",
    };
}

/// <summary>
/// Reports a setting and where it came from.
/// </summary>
/// <remarks>
/// The point of inverting the precedence was not having to go and check the
/// environment. Printing the winner and its source finishes that job: if a
/// value is not what you expected, the run already told you which file or
/// variable to open.
/// </remarks>
static string Configured(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (value is null) return "(unset)";

    // The environment provider is consulted first and overridden by the files,
    // so an environment value that survived is one no file mentioned.
    var fromEnvironment = Environment.GetEnvironmentVariable(key.Replace(":", "__"));

    return fromEnvironment == value && fromEnvironment is not null
        ? $"{value}  (from the environment)"
        : $"{value}  (from appsettings)";
}
