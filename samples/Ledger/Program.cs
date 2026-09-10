using System.ClientModel;
using System.Net.Sockets;

using Agentry;

using Ledger;

// Everything below the two constructor calls is generated. LedgerAnalystAgent
// implements ILedgerAnalyst, takes an AgentRunner, and nothing was registered
// by hand — in a real host both come from DI.
var (client, description) = Model.Resolve();
Console.WriteLine($"model: {description}\n");

ILedgerAnalyst analyst = new LedgerAnalystAgent(new AgentRunner(client));

const string Figures = """
    groceries  spent 259.65  budget 300.00
    coffee     spent  22.80  budget  15.00
    transport  spent  57.75  budget  80.00
    books      spent  31.99  budget  40.00
    """;

try
{
    // Task<string>: text in, text out, no schema and no parsing.
    var summary = await analyst.SummariseAsync(Figures);
    Console.WriteLine($"summary: {summary}\n");

    // Task<Verdict>: the reply is bound to the record. A model that answers in
    // prose fails here with the prose in the message, which is the thing worth
    // seeing when it happens.
    var verdict = await analyst.ReviewAsync(summary, Figures);
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
/// <para>
/// This is the same failure the Python side had — four silent retries under a
/// bug-report banner — and the same fix: unwrap, name the cause, say what to do.
/// </para>
/// </remarks>
static string Explain(Exception error)
{
    // Walk to the innermost cause. AggregateException nests differently from
    // the rest, so flatten it first.
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
