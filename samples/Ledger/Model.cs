using System.ClientModel;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

using OpenAI;

namespace Ledger;

/// <summary>
/// Builds chat clients from configuration.
/// </summary>
/// <remarks>
/// <para>
/// One provider package covers OpenAI, Ollama, vLLM and anything else speaking
/// the OpenAI wire format, because all of them differ only by base address and
/// key. The endpoint is the configuration; the rest is the same code.
/// </para>
/// <para>
/// <b>The credential is not in appsettings.json</b> and should not be. That
/// file is committed, and a plausible-looking value in a committed file is a
/// value somebody pastes a real one over — the same mistake as shipping a
/// working-looking IP in an example env file, which cost an afternoon on the
/// Python side of this. Keys come from the environment or user-secrets, which
/// the configuration builder layers on top.
/// </para>
/// </remarks>
internal sealed class Models(IConfiguration configuration)
{
    private readonly IConfigurationSection _section = configuration.GetSection("Agentry");

    /// <summary>The model used by methods that ask for no particular role.</summary>
    public string DefaultModel => _section["DefaultModel"] ?? "qwen3:latest";

    public string Endpoint => _section["Endpoint"] ?? "http://localhost:11434/v1";

    /// <summary>Every configured role, for reporting what this run will use.</summary>
    public IReadOnlyDictionary<string, string> Roles =>
        _section.GetSection("Models").GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => child.Value!, StringComparer.Ordinal);

    public IChatClient For(string model)
    {
        // Ollama rejects a real key and requires a non-empty one, so the
        // placeholder is the correct value rather than a hack.
        var key = _section["ApiKey"]
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? "ollama";

        return new OpenAIClient(
                new ApiKeyCredential(key),
                new OpenAIClientOptions { Endpoint = new Uri(Endpoint) })
            .GetChatClient(model)
            .AsIChatClient();
    }
}
