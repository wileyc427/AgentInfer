using System.ClientModel;

using AgentInfer;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

using OpenAI;

namespace Ledger;

/// <summary>
/// Builds chat clients from a resolved <see cref="ModelBinding"/>.
/// </summary>
/// <remarks>
/// <para>
/// One provider package covers OpenAI, Ollama, vLLM and anything else speaking
/// the OpenAI wire format, because they differ only by base address and key. A
/// provider that needed a different SDK would be a second branch here, which is
/// exactly where that decision belongs — the library hands over the binding and
/// stays out of it.
/// </para>
/// <para>
/// <b>No credential is in appsettings.json</b> and the file has no field for
/// one. Configuration names <em>where</em> the key lives
/// (<c>ApiKeyVariable</c>); the value comes from the environment. A committed
/// file with a key-shaped field is a file somebody eventually puts a real key
/// in.
/// </para>
/// </remarks>
internal sealed class Models(IConfiguration configuration)
{
    private readonly IConfigurationSection _section = configuration.GetSection("AgentInfer");

    /// <summary>The model used by methods that ask for no particular role.</summary>
    public string DefaultModel => _section["DefaultModel"] ?? "qwen3:latest";

    public string DefaultProvider => _section["DefaultProvider"] ?? "local";

    public string EndpointOf(string provider) =>
        _section[$"Providers:{provider}:Endpoint"] ?? "http://localhost:11434/v1";

    /// <summary>A client for a role the library resolved.</summary>
    public IChatClient For(ModelBinding binding) =>
        Build(binding.Model, binding.Provider.Endpoint, binding.Provider.ApiKey);

    /// <summary>A client for the default runner, which has no role.</summary>
    public IChatClient Default() =>
        Build(DefaultModel, EndpointOf(DefaultProvider), null);

    private static IChatClient Build(string model, string endpoint, string? apiKey) =>
        new OpenAIClient(
                // Ollama rejects a real key and requires a non-empty one, so the
                // placeholder is the correct value rather than a hack.
                new ApiKeyCredential(apiKey ?? "ollama"),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();
}
