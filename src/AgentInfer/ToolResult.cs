using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentInfer;

/// <summary>
/// Turns a tool's return value into the text the model is handed back.
/// </summary>
/// <remarks>
/// <para>
/// Overloads, chosen by the C# compiler from the value's static type, rather
/// than <c>JsonSerializer.Serialize&lt;T&gt;</c> choosing a converter from its
/// run-time type. That call sat one line below the typed argument binding this
/// library is built on: parameters had a compile-time schema and compile-time
/// accessors, and then the result went back out through reflection. It survived
/// because the analyzer that would have said so was never switched on.
/// </para>
/// <para>
/// The set here is deliberately the same set <c>SchemaWriter</c> will describe
/// as a tool parameter — scalars, enums, and arrays of those. A tool returning
/// something richer needs a <c>JsonSerializerContext</c>, and says so at build
/// rather than binding wrongly at run time.
/// </para>
/// <para>
/// Everything writes through <see cref="Utf8JsonWriter"/>, so escaping is the
/// platform's and there is nothing to reflect over.
/// </para>
/// </remarks>
public static class ToolResult
{
    public static string Render(string value) => Write(w => w.WriteStringValue(value));

    public static string Render(bool value) => value ? "true" : "false";

    public static string Render(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Render(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Render(double value) => Write(w => w.WriteNumberValue(value));

    public static string Render(float value) => Write(w => w.WriteNumberValue(value));

    public static string Render(decimal value) => Write(w => w.WriteNumberValue(value));

    /// <summary>
    /// An enum, by name.
    /// </summary>
    /// <remarks>
    /// <c>Enum.GetName&lt;TEnum&gt;</c> is the generic overload, which the
    /// compiler instantiates per enum — so unlike the non-generic one it needs
    /// nothing generated at run time. camelCased to match the wire names the
    /// generator writes into the schema.
    /// </remarks>
    public static string Render<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Render(Camel(Enum.GetName(value) ?? value.ToString()));

    public static string Render(string[] value) => Array(value, static (w, v) => w.WriteStringValue(v));

    public static string Render(bool[] value) => Array(value, static (w, v) => w.WriteBooleanValue(v));

    public static string Render(int[] value) => Array(value, static (w, v) => w.WriteNumberValue(v));

    public static string Render(long[] value) => Array(value, static (w, v) => w.WriteNumberValue(v));

    public static string Render(double[] value) => Array(value, static (w, v) => w.WriteNumberValue(v));

    public static string Render(float[] value) => Array(value, static (w, v) => w.WriteNumberValue(v));

    public static string Render(decimal[] value) => Array(value, static (w, v) => w.WriteNumberValue(v));

    public static string Render<TEnum>(TEnum[] value)
        where TEnum : struct, Enum =>
        Array(value, static (w, v) => w.WriteStringValue(Camel(Enum.GetName(v) ?? v.ToString())));

    /// <summary><c>void</c> and <c>Task</c> tools, which have nothing to say.</summary>
    /// <remarks>
    /// A word rather than an empty string. A tool that ran and returned nothing
    /// is not the same as a tool that returned nothing because it failed, and
    /// the model reads the difference.
    /// </remarks>
    public static string Done() => "done";

    private static string Array<T>(T[] value, Action<Utf8JsonWriter, T> write) =>
        Write(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in value) write(writer, item);
            writer.WriteEndArray();
        });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            body(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Camel(string name) =>
        name.Length > 0 && char.IsUpper(name[0])
            ? char.ToLowerInvariant(name[0]) + name.Substring(1)
            : name;
}
