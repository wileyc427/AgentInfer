using System.ClientModel;

using Microsoft.Extensions.AI;

using OpenAI;

namespace Ledger;

/// <summary>
/// Turns whatever is in the environment into one <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// One provider package, not one per provider. OpenAI, Ollama, vLLM and every
/// other OpenAI-shaped endpoint differ only by base address and key, so the
/// endpoint is the configuration and the rest is the same code.
/// </para>
/// <para>
/// Every resolved value is reported with <b>the setting that produced it</b>.
/// That is not decoration: on the Python side a stale <c>OLLAMA_HOST</c> sent
/// every request to a machine that was not running anything while the local
/// daemon was fine, and the log said only that the connection failed. An
/// address printed without its provenance is one you cannot argue with.
/// </para>
/// </remarks>
internal static class Model
{
    private const string OllamaEndpoint = "http://localhost:11434/v1";

    /// <summary>A client for one named model, on the same endpoint.</summary>
    public static IChatClient For(string model)
    {
        var (_, endpoint, key) = Endpoint();
        return new OpenAIClient(new ApiKeyCredential(key ?? "ollama"),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static (string? Model, string Endpoint, string? Key) Endpoint()
    {
        var endpoint = Environment.GetEnvironmentVariable("AGENTRY_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("AGENTRY_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        endpoint ??= key is not null ? "https://api.openai.com/v1" : OllamaEndpoint;
        return (Environment.GetEnvironmentVariable("AGENTRY_MODEL"), endpoint, key);
    }

    public static (IChatClient Client, string Description) Resolve()
    {
        var model = Environment.GetEnvironmentVariable("AGENTRY_MODEL");
        var endpoint = Environment.GetEnvironmentVariable("AGENTRY_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("AGENTRY_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        string source;

        if (endpoint is not null)
        {
            source = "AGENTRY_ENDPOINT";
        }
        else if (key is not null)
        {
            endpoint = "https://api.openai.com/v1";
            source = "OPENAI_API_KEY present";
        }
        else
        {
            // No key means a local model, and local means Ollama's
            // OpenAI-compatible surface. Note the /v1: it is present here and
            // absent from Ollama's native API, and getting it backwards is a
            // 404 from the server rather than an error from the client.
            endpoint = OllamaEndpoint;
            source = "default (no credential set)";
        }

        model ??= endpoint == OllamaEndpoint ? "qwen3:latest" : "gpt-5-mini";

        // Ollama rejects requests carrying a real key and requires a non-empty
        // one, so a placeholder is the correct value rather than a hack.
        var credential = new ApiKeyCredential(key ?? "ollama");
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };

        var client = new OpenAIClient(credential, options)
            .GetChatClient(model)
            .AsIChatClient();

        return (client, $"{model} at {endpoint} ({source})");
    }
}
