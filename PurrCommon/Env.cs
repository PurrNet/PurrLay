using System.Globalization;
using System.Text;

namespace PurrCommon;

public static class Env
{
    static readonly Dictionary<string, string> _env = new();
    static readonly object _fileLock = new();
    const string ENV_FILE = ".env";

    static Env()
    {
        // Create an empty .env file on first run so users can edit it
        if (!File.Exists(ENV_FILE))
        {
            try
            {
                File.WriteAllText(ENV_FILE, string.Empty);
            }
            catch
            {
                // ignore any IO issues, just proceed without .env
            }
            return;
        }

        LoadEnvFile();
    }

    static void LoadEnvFile()
    {
        var lines = File.ReadAllLines(ENV_FILE);
        string? key = null;
        StringBuilder? valueBuilder = null;
        bool insideMultiline = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (!insideMultiline)
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;

                key = line[..index].Trim();
                var value = line[(index + 1)..].Trim();

                if (value.StartsWith('"') && !value.EndsWith("\""))
                {
                    insideMultiline = true;
                    valueBuilder = new StringBuilder();
                    valueBuilder.AppendLine(value[1..]); // remove opening "
                }
                else
                {
                    _env[key] = value.Trim('"');
                }
            }
            else
            {
                // Keep collecting multiline value
                if (line.EndsWith('\"'))
                {
                    insideMultiline = false;
                    valueBuilder!.AppendLine(line[..^1]); // remove closing "
                    _env[key!] = valueBuilder.ToString().Trim();
                    key = null;
                    valueBuilder = null;
                }
                else
                {
                    valueBuilder!.AppendLine(line);
                }
            }
        }
    }

    static void EnsureKeyInFile(string key)
    {
        EnsureKeyInFile(key, null);
    }

    static void EnsureKeyInFile(string key, string? defaultValue, bool overwriteIfEmpty = true)
    {
        try
        {
            lock (_fileLock)
            {
                if (!File.Exists(ENV_FILE))
                {
                    try { File.WriteAllText(ENV_FILE, string.Empty); }
                    catch { return; }
                }

                var lines = File.ReadAllLines(ENV_FILE).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                        continue;
                    var idx = trimmed.IndexOf('=');
                    if (idx <= 0) continue;
                    var existingKey = trimmed[..idx].Trim();
                    if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        var existingValue = trimmed[(idx + 1)..];
                        if (overwriteIfEmpty && string.IsNullOrEmpty(existingValue) && defaultValue != null)
                        {
                            lines[i] = key + "=" + defaultValue;
                            File.WriteAllLines(ENV_FILE, lines);
                            _env[key] = defaultValue;
                        }
                        return; // already present
                    }
                }

                // Not present then append
                var toAppend = key + "=" + (defaultValue ?? string.Empty);
                File.AppendAllText(ENV_FILE, (lines.Count > 0 ? Environment.NewLine : string.Empty) + toAppend + Environment.NewLine);
                if (defaultValue != null)
                    _env[key] = defaultValue;
            }
        }
        catch
        {
            // ignore IO errors
        }
    }

    static string? GetToken(string key)
    {
        var token = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
        if (!string.IsNullOrEmpty(token)) return token;

        token = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(token)) return token;

        token = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrEmpty(token)) return token;

        return null;
    }

    public static bool TryGetInt(string key, out int value)
    {
        if (TryGetValue(key, out var stringValue) && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        value = 0;
        return false;
    }

    public static bool TryGetValue(string key, out string? value)
    {
        var token = GetToken(key);
        if (!string.IsNullOrEmpty(token))
        {
            value = token;
            return true;
        }

        if (_env.TryGetValue(key, out var fileValue) && !string.IsNullOrEmpty(fileValue))
        {
            value = fileValue;
            return true;
        }

        // Not found or empty then ensure it's listed in .env for the user to fill and return false
        EnsureKeyInFile(key);
        value = null;
        return false;
    }

    public static string TryGetValueOrDefault(string key, string defaultValue)
    {
        if (TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            return value;

        // Persist default into .env if not present
        EnsureKeyInFile(key, defaultValue);
        return defaultValue;
    }

    public static int TryGetIntOrDefault(string key, int defaultValue)
    {
        if (TryGetInt(key, out var value))
            return value;

        // Persist default into .env if not present
        EnsureKeyInFile(key, defaultValue.ToString(CultureInfo.InvariantCulture));
        return defaultValue;
    }
}
