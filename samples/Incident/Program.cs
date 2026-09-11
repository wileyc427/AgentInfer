using System.ClientModel;

using Agentry;

using Incident;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenAI;

// Four agentic workflow patterns, composed with await, switch, Task.WhenAll
// and for — and no framework surface at all. The only thing this library adds
// to the composition is the number printed after each one.
//
//   dotnet run --project samples/Incident            # scripted model, runs anywhere
//   dotnet run --project samples/Incident -- --live  # a real endpoint

var live = args.Contains("--live", StringComparer.Ordinal);

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

var section = configuration.GetSection("Agentry");

string EndpointOf(string provider) =>
    section[$"Providers:{provider}:Endpoint"] ?? "http://localhost:11434/v1";

using var logs = LoggerFactory.Create(builder => builder
    // Information, not Warning. AgentRunner logs one line per generation call
    // — "1 of 2 tools offered, 4 call(s) — TotalFor×4" — and it is the most
    // diagnostic output this library produces. At Warning the sample ran
    // silently and told you nothing about what the model actually did.
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options => options.SingleLine = true));

// Nothing listens to a Meter by default, so every instrument in this library
// records into a void. --metrics prints the distributions at exit; a real host
// points OpenTelemetry at the "Agentry" meter instead.
var metrics = args.Contains("--metrics", StringComparer.Ordinal) ? new Meters() : null;

// One scripted instance, shared: it counts review rounds, so the critic
// rejecting once and then approving needs the same object both times.
var rehearsal = new Rehearsal();

// The library resolves a role to a ModelBinding — a model name and the
// provider it lives on — and hands it over. Building the client is
// provider-specific and stays here, which is why the factory is a lambda
// rather than a configuration key.
IChatClient ClientFor(ModelBinding binding) =>
    live ? Live(binding.Model, binding.Provider.Endpoint, binding.Provider.ApiKey) : rehearsal;

var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(logs);
services.AddLogging();

// ValidateRoles fails here, at startup, if a role the code asks for has no
// model configured — rather than on the first request that needs it. The array
// is generated from the [Model] attributes actually present.
var agentry = services
    .AddAgentryModels(configuration, (binding, _) => ClientFor(binding))
    .ValidateRoles(AgentryRoles.All);

var provider = services.BuildServiceProvider();
var router = provider.GetRequiredService<IModelRouter>();

// Methods with no [Model] use this one.
var runner = new AgentRunner(
    live
        ? Live(section["DefaultModel"] ?? "qwen3:latest",
               EndpointOf(section["DefaultProvider"] ?? "local"),
               null)
        : rehearsal,
    logs.CreateLogger<AgentRunner>());

// This caller may read telemetry. It may not restart a service and it may not
// wake anybody. Both of those are declared on tools, so the model is never
// told they exist — absent from the menu, not refused after asking.
var caller = new GrantedPermissions(["incident.read"]);

var telemetry = new Telemetry();

ITriage triage = new TriageAgent(runner);

IServiceInvestigator investigator = new ServiceInvestigatorAgent(
    runner, new ServiceInvestigatorAgentTools(telemetry), caller);

IPostmortem postmortem = new PostmortemAgent(runner, router);

var investigators = new Investigators(investigator, telemetry);

IIncidentCommander commander = new IncidentCommanderAgent(
    runner, new IncidentCommanderAgentTools(investigators), caller);

var workflows = new Workflows(triage, investigator, postmortem, commander, telemetry);

const string Alert =
    "checkout-api 5xx rate above 40% for 12 minutes; payments p99 latency 12s. "
    + "Started 02:10 UTC. Customers report failed orders.";

if (live)
{
    // Which model each method will actually reach, and from where. A run that
    // does not say this is a run you cannot argue with when the answer looks
    // wrong.
    Console.WriteLine($"default:  {Configured(configuration, "Agentry:DefaultModel")}");
    foreach (var (role, binding) in agentry.Models)
    {
        Console.WriteLine(
            $"role {role}: {binding.Model} at {binding.Provider.Endpoint}  (key: {binding.Provider.KeySource})");
    }

    Console.WriteLine();
}
else
{
    Console.WriteLine("model: scripted (pass --live for a real one)\n");
}

var offered = new ServiceInvestigatorAgentTools(telemetry).AvailableTo(caller);
Console.WriteLine(
    $"telemetry tools offered: {string.Join(", ", offered.Select(t => t.Name))} "
    + $"(of {ServiceInvestigatorAgent.Tools.Tools.Count}; Restart needs a permission this caller lacks)");

var paging = new IncidentCommanderAgentTools(investigators).AvailableTo(caller);
Console.WriteLine(
    $"commander tools offered: {string.Join(", ", paging.Select(t => t.Name))} "
    + "(Page needs incident.page, so nothing can decide to wake anyone)\n");

try
{
    Console.WriteLine("── routing ──────────────────────────────────────────────");
    Console.WriteLine("a closed return type is a switch the compiler checks\n");
    Console.WriteLine($"  {await workflows.TriageAsync(Alert)}\n");

    Console.WriteLine("── parallel sectioning ──────────────────────────────────");
    Console.WriteLine("Task.WhenAll over an interface; the bound is on the scope\n");

    var findings = await workflows.SectionAsync();
    foreach (var finding in findings)
    {
        Console.WriteLine($"  {finding.Service}: healthy={finding.Healthy} ({finding.Confidence}%)");
    }

    Console.WriteLine();
    Console.WriteLine("── evaluator–optimizer ──────────────────────────────────");
    Console.WriteLine("a writer and a critic, bounded twice: attempts and requests\n");

    var report = await workflows.PostmortemAsync(Alert, findings);
    Console.WriteLine($"\n  {report}\n");

    Console.WriteLine("── orchestrator–workers ─────────────────────────────────");
    Console.WriteLine("an agent reached as another agent's tool; counts roll up\n");
    Console.WriteLine($"  {await workflows.CommandAsync(Alert)}\n");

    Console.WriteLine(investigators.Paged.Count == 0
        ? "nobody was paged: Page was never on the menu."
        : $"paged: {string.Join("; ", investigators.Paged)}");
}
catch (AgentBudgetExceededException error)
{
    // A workflow that spent its budget has not answered the question, and
    // saying so is the only outcome that cannot be mistaken for one that did.
    Console.Error.WriteLine($"\n{error.Message}");
    return 2;
}
catch (AgentException error)
{
    // The model answered but the reply would not bind, or failed validation.
    // The message carries what it actually said.
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

IChatClient Live(string model, string endpoint, string? apiKey) =>
    new OpenAIClient(
            // Ollama rejects a real key and requires a non-empty one, so the
            // placeholder is the correct value rather than a hack.
            new ApiKeyCredential(apiKey ?? "ollama"),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
        .GetChatClient(model)
        .AsIChatClient();

/// <summary>Turns a transport failure into one readable line.</summary>
static string Explain(Exception error)
{
    var cause = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;
    while (cause.InnerException is { } inner) cause = inner;

    return cause switch
    {
        System.Net.Sockets.SocketException or HttpRequestException =>
            $"Could not reach the endpoint ({cause.Message}).\n"
            + "Start it with `ollama serve`, or drop --live to use the scripted model.",

        ClientResultException { Status: 401 or 403 } =>
            "The endpoint refused the credential. Set OPENAI_API_KEY, or clear it for a local model.",

        ClientResultException { Status: 404 } =>
            "The endpoint answered but does not have that model. Check `ollama list`.",

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
