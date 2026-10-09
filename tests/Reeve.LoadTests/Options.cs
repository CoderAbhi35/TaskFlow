using System.Globalization;

namespace Reeve.LoadTests;

/// <summary><c>command --key value --flag</c> command-line options.</summary>
internal sealed class Options
{
    public const string DefaultDb = "Host=localhost;Port=5432;Database=reeve;Username=reeve;Password=reeve";

    private readonly Dictionary<string, string> _values;

    private Options(string command, Dictionary<string, string> values) => (Command, _values) = (command, values);

    public string Command { get; }

    public IReadOnlyDictionary<string, string> All => _values;

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            values[key] = hasValue ? args[++i] : "true";
        }

        return new Options(args.Length > 0 ? args[0] : "", values);
    }

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string key, double fallback) =>
        _values.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public bool Flag(string key) => _values.TryGetValue(key, out var v) && v != "false";
}
