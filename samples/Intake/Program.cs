using System.ClientModel;

using Agentry;

using Intake;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenAI;

// A hand-written agent over generated tools.
//
// The other two samples generate the whole agent. This one generates only the
// half that pays — schemas, permissions, dispatch — and writes the class by
// hand, because three of its methods could not have been declared: a prompt
// built from a tenant's policy at run time, a model role chosen from the size
// of the input, and a retry that feeds a binding failure back to the model.
//
//   dotnet run --project samples/Intake            # scripted model
//   dotnet run --project samples/Intake -- --live  # a real endpoint

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

var rehearsal = new Rehearsal();

IChatClient ClientFor(ModelBinding binding) =>
    live ? Live(binding.Model, binding.Provider.Endpoint, binding.Provider.ApiKey) : rehearsal;

var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(logs);
services.AddLogging();

// IntakeRoles.All rather than the generated AgentryRoles.All, and that is the
// cost of writing the class: the generated array is built from the [Model]
// attributes actually present, so it cannot be out of date. This one is what
// somebody remembered to put in it.
var agentry = services
    .AddAgentryModels(configuration, (binding, _) => ClientFor(binding))
    .ValidateRoles(IntakeRoles.All);

var provider = services.BuildServiceProvider();
var router = provider.GetRequiredService<IModelRouter>();

// Read at run time, which is the reason this agent is not generated. In a real
// host it is a row in a table and changes without a deploy.
var policy = new TenantPolicy(
    section["Tenant"] ?? "Northbank Software",
    section["EscalationRules"] ?? "Billing disputes over two cycles go to a human the same day.");

// This caller may read tickets. It may not see charges and it may not close
// anything, so the model is never told those tools exist.
var caller = new GrantedPermissions(["intake.read"]);

// Generated: the invoker, its manifest, every schema, and the dispatch switch.
var tools = new IntakeToolsInvoker(new IntakeTools());

// Hand-written: this one line, and the class behind it.
IIntake intake = new IntakeAgent(router, tools, caller, policy);

Console.WriteLine(live ? "model: live\n" : "model: scripted (pass --live for a real one)\n");

foreach (var (role, binding) in agentry.Models)
{
    Console.WriteLine($"role {role}: {binding.Model} at {binding.Provider.Endpoint}");
}

Console.WriteLine();
Console.WriteLine(
    $"tools offered: {string.Join(", ", tools.AvailableTo(caller).Select(t => t.Name))} "
    + $"(of {IntakeToolsInvoker.Tools.Tools.Count}; Charges and Close need permissions this caller lacks)\n");

const string Ticket =
    "We were charged twice for the March seat licence and once for a seat we removed in February. "
    + "Need both refunded and the seat count corrected before the next cycle.";

try
{
    using var scope = AgentScope.Begin("intake.triage", maxRequests: 20);

    // Routing. The role is chosen from the input, which is the thing [Model]
    // cannot say.
    var category = await intake.ClassifyAsync(Ticket);
    Console.WriteLine($"category: {category}  (short ticket, so the fast role)");

    var summary = await intake.SummariseAsync("T-1041");
    Console.WriteLine($"summary:  {summary}");

    // The scripted model answers urgency 9 out of a declared 1-5 the first
    // time. It binds cleanly — 9 is a perfectly good integer — and [Range]
    // catches it after, which is what the repair loop then reacts to.
    var extract = await intake.ExtractAsync("T-1041");

    Console.WriteLine();
    Console.WriteLine($"customer: {extract.Customer}");
    Console.WriteLine($"category: {extract.Category}");
    Console.WriteLine($"urgency:  {extract.Urgency}/5");
    foreach (var ask in extract.Asks)
    {
        Console.WriteLine($"  · {ask}");
    }

    Console.WriteLine();
    Console.WriteLine($"  {scope}");
    Console.WriteLine("  (one more request than there are methods: the extract was repaired once)");
}
catch (AgentBudgetExceededException error)
{
    Console.Error.WriteLine($"\n{error.Message}");
    return 2;
}
catch (AgentException error)
{
    // Two failures in a row on the same schema. The message carries what the
    // model actually said, which is the thing worth seeing.
    Console.Error.WriteLine($"\n{error.Message}");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"\n{Explain(error)}");
    return 1;
}

return 0;

IChatClient Live(string model, string endpoint, string? apiKey) =>
    new OpenAIClient(
            // Ollama rejects a real key and requires a non-empty one, so the
            // placeholder is the correct value rather than a hack.
            new ApiKeyCredential(apiKey ?? "ollama"),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
        .GetChatClient(model)
        .AsIChatClient();

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

        _ => $"{cause.GetType().Name}: {cause.Message}",
    };
}
