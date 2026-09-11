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

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var section = configuration.GetSection("Agentry");

using var logs = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Warning)
    .AddSimpleConsole(options => options.SingleLine = true));

// One scripted instance, shared: it counts review rounds, so the critic
// rejecting once and then approving needs the same object both times.
var rehearsal = new Rehearsal();

IChatClient ClientFor(string model) => live ? Live(model) : rehearsal;

var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(logs);
services.AddLogging();

// ValidateRoles fails here, at startup, if a role the code asks for has no
// model configured — rather than on the first request that needs it. The array
// is generated from the [Model] attributes actually present.
services
    .AddAgentryModels(configuration, (model, _) => ClientFor(model))
    .ValidateRoles(AgentryRoles.All);

var provider = services.BuildServiceProvider();
var router = provider.GetRequiredService<IModelRouter>();

var runner = new AgentRunner(
    ClientFor(section["DefaultModel"] ?? "qwen3:latest"),
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

Console.WriteLine(live ? $"model: {section["Endpoint"]}\n" : "model: scripted (pass --live for a real one)\n");

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

return 0;

IChatClient Live(string model)
{
    var key = section["ApiKey"]
              ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
              ?? "ollama";   // Ollama rejects a real key and requires a non-empty one.

    return new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(section["Endpoint"] ?? "http://localhost:11434/v1") })
        .GetChatClient(model)
        .AsIChatClient();
}

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
