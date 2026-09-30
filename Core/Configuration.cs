using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Syntax;

namespace ProviderSwitch.Core;

// Edit syntax spans rather than rewriting TOML: comments, tables and other settings survive.
public static class Configuration
{
    public static DocumentSyntax Parse(string text)
    {
        var document = Toml.Parse(text);
        if (document.HasErrors)
            throw new InvalidOperationException("config.toml is invalid. Fix it before switching. No settings were changed.");
        return document;
    }

    private static KeyValueSyntax? Find(DocumentSyntax doc, string name) =>
        doc.KeyValues.FirstOrDefault(k => k.Key?.DotKeys.ChildrenCount == 0 &&
            (k.Key.Key is StringValueSyntax s ? s.Value : k.Key.Key?.ToString().Trim()) == name);

    public static string? Literal(string text, string key)
    {
        var value = Find(Parse(text), key)?.Value;
        return value == null ? null : text.Substring(value.Span.Offset, value.Span.Length);
    }

    public static string? String(string text, string key)
    {
        var model = Parse(text).ToModel();
        if (!model.TryGetValue(key, out var value)) return null;
        return value as string ?? throw new InvalidOperationException($"The '{key}' setting must be a string.");
    }

    public static string Set(string text, string key, string? literal)
    {
        var node = Find(Parse(text), key);
        string edited;
        if (node == null)
            edited = literal == null ? text : key + " = " + literal + (text.Contains("\r\n") ? "\r\n" : "\n") + text;
        else if (literal != null)
            edited = text.Remove(node.Value!.Span.Offset, node.Value.Span.Length).Insert(node.Value.Span.Offset, literal);
        else
        {
            // Remove key/value tokens only, retaining the line's comment and surrounding trivia.
            int start = node.Key!.Span.Offset;
            int end = node.Value!.Span.Offset + node.Value.Span.Length;
            int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
            int newline = text.IndexOf('\n', end);
            int lineEnd = newline < 0 ? text.Length : newline;
            bool emptyLine = string.IsNullOrWhiteSpace(text[lineStart..start]) && string.IsNullOrWhiteSpace(text[end..lineEnd]);
            edited = emptyLine ? text.Remove(lineStart, (newline < 0 ? lineEnd : newline + 1) - lineStart) : text.Remove(start, end - start);
        }
        Parse(edited);
        return edited;
    }

    public static string Quote(string value) => JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

public static class EnvSettings
{
    private static Regex Pattern(string key) => new(@"(?m)^[ \t]*(?:export[ \t]+)?" + Regex.Escape(key) + @"[ \t]*=[^\r\n]*");
    public static string? GetLine(string text, string key)
    {
        var matches = Pattern(key).Matches(text);
        if (matches.Count > 1) throw new InvalidOperationException($".env contains multiple {key} entries. Resolve them before switching.");
        return matches.Count == 0 ? null : matches[0].Value;
    }
    public static string SetLine(string text, string key, string? rawLine)
    {
        var line = GetLine(text, key);
        if (line != null)
        {
            var match = Pattern(key).Match(text);
            if (rawLine != null) return text.Remove(match.Index, match.Length).Insert(match.Index, rawLine);
            int end = match.Index + match.Length;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
            return text.Remove(match.Index, end - match.Index);
        }
        if (rawLine == null) return text;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return text + (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + rawLine + newline;
    }
    public static bool ContainsToken(string text) => GetLine(text, "AWS_BEARER_TOKEN_BEDROCK") is string line &&
        !Regex.IsMatch(line, @"=[ \t]*(?:""""|'')?[ \t]*(?:#.*)?$");
}

public sealed class SavedState
{
    public bool HasOpenAiSnapshot { get; set; }
    public string? OpenAiProviderLiteral { get; set; }
    public string? OpenAiModelLiteral { get; set; }
    public bool HasServiceTierSnapshot { get; set; }
    public string? OpenAiServiceTierLiteral { get; set; }
    public string? OriginalRegionLine { get; set; }
    public string? OriginalProfileLine { get; set; }
    public string? OriginalDefaultRegionLine { get; set; }
    public string Region { get; set; } = "us-east-2";
    public string Profile { get; set; } = "default";
    public string BedrockModel { get; set; } = "";
}

public record Status(string Provider, string? Model, bool HasAwsToken, SavedState State);

public sealed class SwitchService
{
    public string Home { get; }
    public string ConfigPath => Path.Combine(Home, "config.toml");
    public string EnvPath => Path.Combine(Home, ".env");
    public string DataPath => Path.Combine(Home, ".provider-switch");
    private string StatePath => Path.Combine(DataPath, "state.json");
    private readonly Action<string>? beforeCommitWrite;

    public SwitchService(string home) => Home = Path.GetFullPath(home);
    // Deterministic failure injection is available only to the check assembly.
    internal SwitchService(string home, Action<string> beforeCommitWrite) : this(home) => this.beforeCommitWrite = beforeCommitWrite;
    public static string DefaultHome => Environment.GetEnvironmentVariable("CODEX_HOME") is string home && home.Length > 0
        ? Path.GetFullPath(home) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static string Read(string path)
    {
        if (Directory.Exists(path)) throw new IOException("A settings file path is occupied by a directory: " + Path.GetFileName(path));
        if (!File.Exists(path)) return "";
        byte[] bytes = File.ReadAllBytes(path);
        int offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new InvalidOperationException(Path.GetFileName(path) + " must use valid UTF-8 encoding. No settings were changed."); }
    }
    private SavedState Load()
    {
        SavedState state;
        try { state = File.Exists(StatePath) ? JsonSerializer.Deserialize<SavedState>(Read(StatePath)) ?? throw new InvalidOperationException("The saved switch settings are invalid.") : new(); }
        catch (JsonException) { throw new InvalidOperationException("The saved switch settings are invalid. Restore a valid state.json before switching."); }
        if (state.HasOpenAiSnapshot)
        {
            ValidateLiteral(state.OpenAiProviderLiteral, "provider", "openai");
            ValidateLiteral(state.OpenAiModelLiteral, "model");
        }
        if (state.HasServiceTierSnapshot) ValidateLiteral(state.OpenAiServiceTierLiteral, "service tier");
        foreach (var (key, line) in new[] { ("AWS_REGION", state.OriginalRegionLine), ("AWS_PROFILE", state.OriginalProfileLine), ("AWS_DEFAULT_REGION", state.OriginalDefaultRegionLine) })
            if (line != null && (line.Contains('\r') || line.Contains('\n') || EnvSettings.GetLine(line, key) != line))
                throw new InvalidOperationException("The saved AWS settings are invalid. No settings were changed.");
        return state;
    }
    private static void ValidateLiteral(string? literal, string label, string? required = null)
    {
        if (literal == null) return;
        var doc = Configuration.Parse("value = " + literal + "\n");
        if (doc.KeyValues.ChildrenCount != 1 || doc.Tables.ChildrenCount != 0 || doc.KeyValues.First().Value is not StringValueSyntax value ||
            (required != null && value.Value != required))
            throw new InvalidOperationException("The saved " + label + " setting is invalid. No settings were changed.");
    }

    public Status Inspect()
    {
        GuardPaths();
        var config = Read(ConfigPath);
        var state = Load();
        return new(Configuration.String(config, "model_provider") ?? "openai", Configuration.String(config, "model"),
            EnvSettings.ContainsToken(Read(EnvPath)) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK")), state);
    }

    public void Switch(bool bedrock, string region, string profile, string model)
    {
        region = region.Trim(); profile = profile.Trim(); model = model.Trim();
        if (bedrock)
        {
            if (!Regex.IsMatch(region, @"^[a-z]{2}(?:-[a-z0-9]+)+-\d+$"))
                throw new InvalidOperationException("Enter an AWS region such as us-east-2.");
            if (!Regex.IsMatch(profile, @"^[A-Za-z0-9_.-]+$"))
                throw new InvalidOperationException("Enter a named AWS profile using letters, numbers, dots, underscores or hyphens.");
            if (model.Length > 0 && !Regex.IsMatch(model, @"^[A-Za-z0-9._:/-]+$"))
                throw new InvalidOperationException("Enter a Bedrock inference profile ID, or leave the model blank to use the app's picker.");
        }
        GuardPaths();
        Directory.CreateDirectory(DataPath);
        using var operationLock = new FileStream(Path.Combine(DataPath, "switch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var config = Read(ConfigPath);
        var env = Read(EnvPath);
        var state = Load();
        var current = Configuration.String(config, "model_provider") ?? "openai";
        if (!string.IsNullOrEmpty(Configuration.String(config, "profile")))
            throw new InvalidOperationException("Your config selects a named Codex profile. That profile may override this switch. Remove the root 'profile' selection or use separate CLI profiles instead.");
        if (current != "openai" && current != "amazon-bedrock-runtime")
            throw new InvalidOperationException("Your current provider is '" + current + "'. This utility switches only OpenAI and Bedrock Runtime. Save your custom setup separately first.");
        if (bedrock)
        {
            if (current == "openai")
            {
                state.HasOpenAiSnapshot = true;
                state.OpenAiProviderLiteral = Configuration.Literal(config, "model_provider");
                state.OpenAiModelLiteral = Configuration.Literal(config, "model");
                state.HasServiceTierSnapshot = true;
                state.OpenAiServiceTierLiteral = Configuration.Literal(config, "service_tier");
                state.OriginalRegionLine = EnvSettings.GetLine(env, "AWS_REGION");
                state.OriginalProfileLine = EnvSettings.GetLine(env, "AWS_PROFILE");
                state.OriginalDefaultRegionLine = EnvSettings.GetLine(env, "AWS_DEFAULT_REGION");
            }
            else if (state.HasOpenAiSnapshot && !state.HasServiceTierSnapshot)
            {
                // Version 1.0 left service_tier unchanged in Bedrock mode. Capture it when upgrading.
                state.HasServiceTierSnapshot = true;
                state.OpenAiServiceTierLiteral = Configuration.Literal(config, "service_tier");
            }
            state.Region = region; state.Profile = profile; state.BedrockModel = model;
        }
        else if (current == "openai") return;

        string nextConfig = Configuration.Set(config, "model_provider", bedrock ? Configuration.Quote("amazon-bedrock-runtime") :
            state.HasOpenAiSnapshot ? state.OpenAiProviderLiteral : Configuration.Quote("openai"));
        nextConfig = Configuration.Set(nextConfig, "model", bedrock ? model.Length == 0 ? null : Configuration.Quote(model) :
            state.HasOpenAiSnapshot ? state.OpenAiModelLiteral : null);
        if (bedrock || state.HasServiceTierSnapshot)
            nextConfig = Configuration.Set(nextConfig, "service_tier", bedrock ? null : state.OpenAiServiceTierLiteral);
        var nextEnv = env;
        if (bedrock || state.HasOpenAiSnapshot)
        {
            nextEnv = EnvSettings.SetLine(nextEnv, "AWS_REGION", bedrock ? "AWS_REGION=" + region : state.OriginalRegionLine);
            nextEnv = EnvSettings.SetLine(nextEnv, "AWS_PROFILE", bedrock ? "AWS_PROFILE=" + profile : state.OriginalProfileLine);
            nextEnv = EnvSettings.SetLine(nextEnv, "AWS_DEFAULT_REGION", bedrock ? "AWS_DEFAULT_REGION=" + region : state.OriginalDefaultRegionLine);
        }
        var nextState = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        // Keep complete originals in memory for rollback. No auth.json or AWS credential files are opened.
        var originals = new Dictionary<string, byte[]?>();
        foreach (var path in new[] { ConfigPath, EnvPath, StatePath })
            originals[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (Read(ConfigPath) != config || Read(EnvPath) != env)
            throw new IOException("Settings changed while preparing the switch. Try again.");
        var backups = Path.Combine(DataPath, "backups");
        Directory.CreateDirectory(backups);
        if (originals[ConfigPath] is byte[] bytes)
            File.WriteAllBytes(Path.Combine(backups, "config-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".toml"), bytes);
        var written = new List<string>();
        void Commit(string path, byte[] bytes)
        {
            beforeCommitWrite?.Invoke(path);
            WriteAtomic(path, bytes);
            written.Add(path);
        }
        try
        {
            Commit(StatePath, Encoding.UTF8.GetBytes(nextState));
            if (nextEnv != env) Commit(EnvPath, PreserveBom(originals[EnvPath], nextEnv));
            Commit(ConfigPath, PreserveBom(originals[ConfigPath], nextConfig));
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var path in written.AsEnumerable().Reverse())
            {
                try
                {
                    if (originals[path] is not byte[] original) File.Delete(path);
                    else WriteAtomic(path, original);
                }
                catch (Exception ex) { rollbackFailures.Add(ex); }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException("The switch failed and some files could not be restored. Use your configuration backup before continuing.", new[] { failure }.Concat(rollbackFailures));
            throw;
        }
    }

    private void GuardPaths()
    {
        foreach (var path in new[] { ConfigPath, EnvPath, StatePath, Path.Combine(DataPath, "switch.lock") })
            if (Directory.Exists(path)) throw new IOException("A settings file path is occupied by a directory: " + Path.GetFileName(path));
        // Refuse links/junctions, including parent directories, to avoid editing redirected settings.
        foreach (var path in new[] { Home, ConfigPath, EnvPath, DataPath, StatePath, Path.Combine(DataPath, "backups"), Path.Combine(DataPath, "switch.lock") })
        {
            for (var p = path; p != null; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A settings path uses a symbolic link or junction. Use a regular local .codex directory for this utility.");
        }
    }
    private static byte[] PreserveBom(byte[]? original, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return original != null && original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? Encoding.UTF8.Preamble.ToArray().Concat(bytes).ToArray() : bytes;
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
