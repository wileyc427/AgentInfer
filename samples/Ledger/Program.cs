using System.ClientModel;
using System.Net.Sockets;

using Agentry;

using Ledger;

var (client, description) = Model.Resolve();
Console.WriteLine($"model: {description}");

// The caller may read the ledger and not write to it. LedgerTools declares a
// Reclassify tool requiring "ledger.write", so the model is never told it
// exists — and would be refused if it asked anyway.
var caller = new GrantedPermissions(["ledger.read"]);
var invoker = new LedgerAnalystAgentTools(new LedgerTools());

Console.WriteLine(
    $"tools: {string.Join(", ", invoker.AvailableTo(caller).Select(t => t.Name))} " +
    $"(of {invoker.Manifest.Tools.Count}; the rest need permissions this caller lacks)\n");

// Generated. In a real host all three come from DI.
ILedgerAnalyst analyst = new LedgerAnalystAgent(new AgentRunner(client), invoker, caller);

try
{
    var summary = await analyst.SummariseAsync();
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
    // The model answered but the reply would not bind. The message carries what
    // it actually said, which is the thing worth seeing.
    Console.Error.WriteLine($"\n{error.Message}");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"\n{Explain(error)}");
    return 1;
}

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
            "Start it with `ollama serve`, or set AGENTRY_ENDPOINT if it listens elsewhere.",

        ClientResultException { Status: 401 or 403 } =>
            "The endpoint refused the credential. Set AGENTRY_API_KEY, or clear it for a local model.",

        ClientResultException { Status: 404 } =>
            "The endpoint answered but does not have that model. Check `ollama list` and set AGENTRY_MODEL.",

        _ => $"{cause.GetType().Name}: {cause.Message}",
    };
}
