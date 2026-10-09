using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Contracts.Serialization;

/// <summary>The wire format shared by the API and its clients (and tests).</summary>
public static class ReeveJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        return options;
    }
}
