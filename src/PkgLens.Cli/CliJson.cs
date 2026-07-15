using System.Text.Json;
using System.Text.Json.Serialization;

namespace PkgLens.Cli;

internal static class CliJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, Options));
}
