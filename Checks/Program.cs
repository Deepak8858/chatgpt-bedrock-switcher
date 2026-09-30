using System.Text;
using ProviderSwitch.Core;
using Tomlyn;
using System.Text.Json;

var checks = new List<(string, Action)>();
void Check(string name, Action test) => checks.Add((name, test));
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); } catch (InvalidOperationException) { return; } catch (IOException) { return; }
    throw new Exception("Expected rejection.");
}
void CreateTestLink(string link, string target)
{
    try { File.CreateSymbolicLink(link, target); }
    catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
    { throw new CheckSkipped("Windows developer mode or symbolic-link privilege is required for this check."); }
}
void WithHome(Action<string, SwitchService> action)
{
    string home = Path.Combine(Path.GetTempPath(), "provider-switch-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(home);
    try { action(home, new SwitchService(home)); }
    finally { Directory.Delete(home, true); }
}

Check("TOML edits preserve comments, Unicode, nested keys and multiline strings", () =>
{
    string config = "# café 🔄\r\n\"model_provider\" = 'openai' # provider note\r\nmodel = \"gpt-6-sol\" # model note\r\nprompt = '''\r\n[pretend]\r\nmodel_provider = 'fake'\r\n'''\r\n[tools.example]\r\nmodel = 'leave me'\r\nmodel_provider = 'leave this too'\r\n";
    string edited = Configuration.Set(config, "model_provider", Configuration.Quote("amazon-bedrock-runtime"));
    Assert(edited == config.Replace("= 'openai' # provider", "= \"amazon-bedrock-runtime\" # provider"), "Unrelated text changed.");
    var reverted = Configuration.Set(edited, "model_provider", Configuration.Literal(config, "model_provider"));
    Assert(reverted == config, "Literal round trip changed formatting.");
    var removed = Configuration.Set(edited, "model", null);
    Assert(Configuration.String(removed, "model") == null, "Root model remained.");
    Assert(removed.Contains("# model note") && removed.Contains("model = 'leave me'"), "Comment or table model lost.");
});

Check("Full OpenAI → Bedrock → OpenAI cycle restores model, AWS settings and login", () => WithHome((home, service) =>
{
    string config = "# preferences\nmodel = 'gpt-6-sol'\nmodel_provider = 'openai'\n[projects.'C:\\work']\ntrust_level = 'trusted'\n";
    string env = "# existing\nexport AWS_REGION='eu-west-1' # existing region\nAWS_PROFILE=company\nAWS_DEFAULT_REGION=eu-west-1\nUNRELATED=keep-this\nAWS_BEARER_TOKEN_BEDROCK='sentinel'\n";
    File.WriteAllText(service.ConfigPath, config); File.WriteAllText(service.EnvPath, env);
    File.WriteAllText(Path.Combine(home, "auth.json"), "login sentinel");
    service.Switch(true, "us-east-2", "work-sso", "global.openai.gpt-6-sol");
    Assert(service.Inspect().Provider == "amazon-bedrock-runtime", "Wrong provider.");
    Assert(service.Inspect().Model == "global.openai.gpt-6-sol", "Wrong Runtime model.");
    Assert(File.ReadAllText(service.EnvPath).Contains("AWS_PROFILE=work-sso"), "AWS profile missing.");
    Assert(File.ReadAllText(service.EnvPath).Contains("AWS_BEARER_TOKEN_BEDROCK='sentinel'"), "Token changed.");
    service.Switch(false, "", "", "");
    Assert(File.ReadAllText(service.ConfigPath) == config, "Original config not restored.");
    Assert(File.ReadAllText(service.EnvPath) == env, "Original AWS settings not restored.");
    Assert(File.ReadAllText(Path.Combine(home, "auth.json")) == "login sentinel", "Login was touched.");
    Assert(Directory.GetFiles(Path.Combine(service.DataPath, "backups")).Length == 2, "Backup missing.");
}));

Check("Default provider and absent model survive repeated switches", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "# default provider\n[features]\nthing = true\n");
    for (int i = 0; i < 3; i++)
    {
        service.Switch(true, "us-east-2", "default", "");
        Assert(service.Inspect().Model == null, "Blank model should use picker.");
        service.Switch(false, "", "", "");
        Assert(Configuration.Literal(File.ReadAllText(service.ConfigPath), "model_provider") == null, "Implicit default changed.");
        Assert(service.Inspect().Model == null, "Implicit model changed.");
    }
    Assert(File.ReadAllText(service.ConfigPath).Contains("[features]\nthing = true"), "Table damaged.");
}));

Check("Changing Bedrock fields preserves the original OpenAI snapshot", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model = 'gpt-6-sol'\n");
    service.Switch(true, "us-east-2", "default", "global.openai.gpt-6-sol");
    service.Switch(true, "us-east-1", "second", "us.openai.gpt-6-astra");
    service.Switch(false, "", "", "");
    Assert(service.Inspect().Model == "gpt-6-sol", "Original model overwritten.");
}));

Check("Paid-plan Fast preference is omitted for Bedrock and restored exactly", () => WithHome((home, service) =>
{
    string config = "model = 'gpt-6-sol'\nservice_tier = 'fast' # paid speed preference\n[features]\nfast_mode = true\n[tools.test]\nenabled = true\n";
    File.WriteAllText(service.ConfigPath, config);
    service.Switch(true, "us-east-2", "default", "");
    Assert(Configuration.Literal(File.ReadAllText(service.ConfigPath), "service_tier") == null, "Unsupported tier sent to Bedrock.");
    Assert(File.ReadAllText(service.ConfigPath).Contains("fast_mode = true"), "Feature preference changed.");
    service.Switch(true, "us-east-1", "default", "");
    service.Switch(false, "", "", "");
    Assert(Configuration.String(File.ReadAllText(service.ConfigPath), "service_tier") == "fast", "Paid speed preference not restored.");
    Assert(File.ReadAllText(service.ConfigPath).Contains("# paid speed preference"), "Speed comment lost.");
    Assert(File.ReadAllText(service.ConfigPath).Contains("[tools.test]\nenabled = true"), "Tool configuration changed.");
}));

Check("Version 1.0 state migrates without losing the saved speed preference", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model_provider = 'amazon-bedrock-runtime'\nservice_tier = 'fast'\n");
    Directory.CreateDirectory(service.DataPath);
    File.WriteAllText(Path.Combine(service.DataPath, "state.json"), "{\"HasOpenAiSnapshot\":true,\"OpenAiModelLiteral\":\"'gpt-6-sol'\"}");
    service.Switch(true, "us-east-2", "default", "");
    Assert(Configuration.Literal(File.ReadAllText(service.ConfigPath), "service_tier") == null, "Legacy tier remained in Bedrock mode.");
    service.Switch(false, "", "", "");
    Assert(service.Inspect().Model == "gpt-6-sol", "Saved legacy model lost.");
    Assert(Configuration.String(File.ReadAllText(service.ConfigPath), "service_tier") == "fast", "Legacy preference lost.");
}));

Check("Invalid TOML, profiles and other providers leave settings unchanged", () =>
{
    foreach (var config in new[] { "model = [broken\n", "profile = 'work'\n", "model_provider = 'proxy'\n", "model_provider = 'amazon-bedrock'\n" })
        WithHome((home, service) =>
        {
            File.WriteAllText(service.ConfigPath, config);
            File.WriteAllText(service.EnvPath, "AWS_REGION=eu-west-1\n");
            Reject(() => service.Switch(true, "us-east-2", "default", ""));
            Assert(File.ReadAllText(service.ConfigPath) == config, "Rejected switch changed config.");
            Assert(File.ReadAllText(service.EnvPath) == "AWS_REGION=eu-west-1\n", "Rejected switch changed env.");
            Assert(!File.Exists(Path.Combine(service.DataPath, "state.json")), "Rejected switch wrote state.");
        });
});

Check("Input injection and duplicate AWS entries are rejected", () => WithHome((home, service) =>
{
    foreach (var values in new[] { ("us-east-2\nINJECT=yes", "default", ""), ("us-east-2", "bad\nPROFILE=yes", ""), ("us-east-2", "default", "bad\nmodel=yes") })
        Reject(() => service.Switch(true, values.Item1, values.Item2, values.Item3));
    File.WriteAllText(service.EnvPath, "AWS_REGION=a\nAWS_REGION=b\n");
    Reject(() => service.Switch(true, "us-east-2", "default", ""));
    Assert(!File.Exists(service.ConfigPath), "Invalid env wrote config.");
}));

Check("UTF-8 BOM is preserved and backups contain exact original bytes", () => WithHome((home, service) =>
{
    var bytes = Encoding.UTF8.Preamble.ToArray().Concat(Encoding.UTF8.GetBytes("model = 'gpt-6-sol'\r\n")).ToArray();
    File.WriteAllBytes(service.ConfigPath, bytes);
    service.Switch(true, "us-east-2", "default", "");
    Assert(File.ReadAllBytes(service.ConfigPath).AsSpan().StartsWith(Encoding.UTF8.Preamble), "BOM lost.");
    Assert(File.ReadAllBytes(Directory.GetFiles(Path.Combine(service.DataPath, "backups"))[0]).SequenceEqual(bytes), "Backup differed.");
}));

Check("Token precedence notice handles empty values", () =>
{
    foreach (var value in new[] { "", "''", "\"\"", " # empty" })
        Assert(!EnvSettings.ContainsToken("AWS_BEARER_TOKEN_BEDROCK=" + value + "\n"), "Empty token detected.");
    Assert(EnvSettings.ContainsToken("export AWS_BEARER_TOKEN_BEDROCK=sentinel\n"), "Token not detected.");
});

Check("Symbolic links are refused without modifying their target", () => WithHome((home, service) =>
{
    string target = Path.Combine(home, "original.toml");
    File.WriteAllText(target, "model = 'gpt-6-sol'\n");
    CreateTestLink(service.ConfigPath, target);
    Reject(() => service.Switch(true, "us-east-2", "default", ""));
    Assert(File.ReadAllText(target) == "model = 'gpt-6-sol'\n", "Linked target changed.");
}));

Check("Existing Bedrock setup can return to default OpenAI without a snapshot", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model_provider = 'amazon-bedrock-runtime'\nmodel = 'global.openai.gpt-6-sol'\n");
    File.WriteAllText(service.EnvPath, "AWS_REGION=us-east-2\n");
    service.Switch(false, "", "", "");
    Assert(service.Inspect().Provider == "openai" && service.Inspect().Model == null, "OpenAI defaults not selected.");
    Assert(File.ReadAllText(service.EnvPath) == "AWS_REGION=us-east-2\n", "Existing AWS env changed.");
}));

Check("Repeated toggles do not grow configuration or environment files", () => WithHome((home, service) =>
{
    string config = "# original\n[features]\nfast_mode = true\n";
    string env = "UNRELATED=preserve\n";
    File.WriteAllText(service.ConfigPath, config); File.WriteAllText(service.EnvPath, env);
    for (int i = 0; i < 50; i++)
    {
        service.Switch(true, "us-east-2", "default", "");
        service.Switch(false, "", "", "");
    }
    Assert(File.ReadAllText(service.ConfigPath) == config, "Configuration accumulates extra text during repeated toggles.");
    Assert(File.ReadAllText(service.EnvPath) == env, "Environment accumulates extra text during repeated toggles.");
}));

Check("Backup names remain unique through rapid repeated switches", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model = 'gpt-6-sol'\n");
    for (int i = 0; i < 10; i++) { service.Switch(true, "us-east-2", "default", ""); service.Switch(false, "", "", ""); }
    Assert(Directory.GetFiles(Path.Combine(service.DataPath, "backups")).Length == 20, "Backups overwritten.");
}));

Check("Switching OpenAI when already selected does not write backups or state", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model = 'gpt-6-sol'\n");
    service.Switch(false, "", "", "");
    Assert(!File.Exists(Path.Combine(service.DataPath, "state.json")), "Unnecessary state created.");
    Assert(!Directory.Exists(Path.Combine(service.DataPath, "backups")), "Unnecessary backup created.");
}));

Check("Refresh inspection does not write any settings", () => WithHome((home, service) =>
{
    var status = service.Inspect();
    Assert(status.Provider == "openai", "Fresh configuration did not use default provider.");
    Assert(Directory.GetFileSystemEntries(home).Length == 0, "Inspection wrote settings.");
}));

Check("External unrelated edits while in Bedrock survive returning to OpenAI", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model = 'gpt-6-sol'\n[features]\nfast_mode = true\n");
    File.WriteAllText(service.EnvPath, "OTHER=before\n");
    service.Switch(true, "us-east-2", "default", "");
    File.AppendAllText(service.ConfigPath, "\n[tools.new_tool]\nenabled = true\n");
    File.AppendAllText(service.EnvPath, "ADDED=after\n");
    service.Switch(false, "", "", "");
    Assert(File.ReadAllText(service.ConfigPath).Contains("[tools.new_tool]\nenabled = true"), "External tool edit lost.");
    Assert(File.ReadAllText(service.EnvPath).Contains("ADDED=after"), "External environment edit lost.");
}));

Check("Invalid UTF-8 input is rejected without corrupting original bytes", () => WithHome((home, service) =>
{
    var bytes = new byte[] { (byte)'#', 0xff, (byte)'\n' };
    File.WriteAllBytes(service.ConfigPath, bytes);
    Reject(() => service.Switch(true, "us-east-2", "default", ""));
    Assert(File.ReadAllBytes(service.ConfigPath).SequenceEqual(bytes), "Invalid encoding changed.");
}));

Check("UTF-16 config is refused rather than silently changing encoding", () => WithHome((home, service) =>
{
    var bytes = Encoding.Unicode.Preamble.ToArray().Concat(Encoding.Unicode.GetBytes("model = 'gpt-6-sol'\n")).ToArray();
    File.WriteAllBytes(service.ConfigPath, bytes);
    Reject(() => service.Switch(true, "us-east-2", "default", ""));
    Assert(File.ReadAllBytes(service.ConfigPath).SequenceEqual(bytes), "UTF-16 config changed.");
}));

Check("Corrupt saved snapshot cannot inject a custom provider on restore", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model_provider = 'amazon-bedrock-runtime'\n");
    Directory.CreateDirectory(service.DataPath);
    var state = new SavedState { HasOpenAiSnapshot = true, OpenAiProviderLiteral = "'proxy'" };
    File.WriteAllText(Path.Combine(service.DataPath, "state.json"), JsonSerializer.Serialize(state));
    Reject(() => service.Switch(false, "", "", ""));
    Assert(Configuration.String(File.ReadAllText(service.ConfigPath), "model_provider") == "amazon-bedrock-runtime", "Invalid snapshot changed provider.");
}));

Check("Corrupt saved literal cannot inject additional TOML settings", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model_provider = 'amazon-bedrock-runtime'\n");
    Directory.CreateDirectory(service.DataPath);
    var state = new SavedState { HasOpenAiSnapshot = true, OpenAiModelLiteral = "'gpt-6-sol'\nweb_search = 'live'" };
    File.WriteAllText(Path.Combine(service.DataPath, "state.json"), JsonSerializer.Serialize(state));
    Reject(() => service.Switch(false, "", "", ""));
    Assert(Configuration.Literal(File.ReadAllText(service.ConfigPath), "web_search") == null, "Injected setting written.");
}));

Check("CRLF environment, exported values and unrelated credentials survive", () => WithHome((home, service) =>
{
    string env = "# original\r\nexport AWS_PROFILE = 'before' # note\r\nAWS_REGION = us-west-2\r\nAWS_DEFAULT_REGION=us-west-2\r\nUNRELATED_TOKEN='synthetic-test-only'\r\n";
    File.WriteAllText(service.EnvPath, env);
    service.Switch(true, "us-east-2", "work-sso", ""); service.Switch(false, "", "", "");
    Assert(File.ReadAllText(service.EnvPath) == env, "CRLF environment did not round-trip.");
}));

Check("Concurrent utility operation is rejected without changing settings", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model = 'gpt-6-sol'\n");
    Directory.CreateDirectory(service.DataPath);
    using var held = new FileStream(Path.Combine(service.DataPath, "switch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Reject(() => service.Switch(true, "us-east-2", "default", ""));
    Assert(File.ReadAllText(service.ConfigPath) == "model = 'gpt-6-sol'\n", "Lock rejection changed config.");
}));

Check("Directories occupying file paths are rejected before state is written", () =>
{
    foreach (var fileName in new[] { "config.toml", ".env", ".provider-switch/state.json" })
        WithHome((home, service) =>
        {
            var path = Path.Combine(home, fileName); Directory.CreateDirectory(path);
            Reject(() => service.Switch(true, "us-east-2", "default", ""));
            Assert(Directory.Exists(path), "Existing directory altered.");
            Assert(!File.Exists(Path.Combine(service.DataPath, "state.json")), "Failed preflight wrote state.");
        });
});

Check("Dangling settings symlink is refused", () => WithHome((home, service) =>
{
    CreateTestLink(service.ConfigPath, Path.Combine(home, "missing.toml"));
    Reject(() => service.Switch(true, "us-east-2", "default", ""));
    Assert(new FileInfo(service.ConfigPath).LinkTarget != null, "Dangling link replaced.");
}));

Check("Config types and duplicate root keys are rejected before writing", () =>
{
    foreach (var config in new[] { "model_provider = 1\n", "profile = []\n", "model_provider = 'openai'\nmodel_provider = 'openai'\n" })
        WithHome((home, service) =>
        {
            File.WriteAllText(service.ConfigPath, config);
            Reject(() => service.Switch(true, "us-east-2", "default", ""));
            Assert(File.ReadAllText(service.ConfigPath) == config, "Rejected type changed settings.");
        });
});

Check("All managed environment duplicates are rejected", () =>
{
    foreach (var key in new[] { "AWS_PROFILE", "AWS_REGION", "AWS_DEFAULT_REGION" })
        WithHome((home, service) =>
        {
            string env = key + "=first\nexport " + key + "=second\n";
            File.WriteAllText(service.EnvPath, env);
            Reject(() => service.Switch(true, "us-east-2", "default", ""));
            Assert(File.ReadAllText(service.EnvPath) == env, "Duplicate rejection changed env.");
        });
});

Check("A write failure at every commit step restores existing files exactly", () =>
{
    foreach (var failName in new[] { "state.json", ".env", "config.toml" })
        WithHome((home, service) =>
        {
            string config = "model = 'gpt-6-sol'\nservice_tier = 'fast'\n";
            string env = "AWS_REGION=eu-west-1\nOTHER=preserve\n";
            Directory.CreateDirectory(service.DataPath);
            string state = JsonSerializer.Serialize(new SavedState { Profile = "previous" });
            File.WriteAllText(service.ConfigPath, config); File.WriteAllText(service.EnvPath, env);
            string statePath = Path.Combine(service.DataPath, "state.json"); File.WriteAllText(statePath, state);
            var failing = new SwitchService(home, path => { if (Path.GetFileName(path) == failName) throw new IOException("Injected transient write failure"); });
            Reject(() => failing.Switch(true, "us-east-2", "default", ""));
            Assert(File.ReadAllText(service.ConfigPath) == config, "Failed write changed config.");
            Assert(File.ReadAllText(service.EnvPath) == env, "Failed write changed env.");
            Assert(File.ReadAllText(statePath) == state, "Failed write changed state.");
            Assert(Directory.GetFiles(home, "*.tmp", SearchOption.AllDirectories).Length == 0, "Temporary files left behind.");
            service.Switch(true, "us-east-2", "default", "");
            Assert(service.Inspect().Provider == "amazon-bedrock-runtime", "Lock not released after write failure.");
        });
});

Check("Failure on a fresh home removes newly committed files", () => WithHome((home, service) =>
{
    var failing = new SwitchService(home, path => { if (path == service.ConfigPath) throw new IOException("Injected final write failure"); });
    Reject(() => failing.Switch(true, "us-east-2", "default", ""));
    Assert(!File.Exists(service.ConfigPath) && !File.Exists(service.EnvPath) && !File.Exists(Path.Combine(service.DataPath, "state.json")), "Partial new configuration left behind.");
}));

Check("Invalid saved AWS lines cannot add environment entries", () => WithHome((home, service) =>
{
    File.WriteAllText(service.ConfigPath, "model_provider = 'amazon-bedrock-runtime'\n");
    Directory.CreateDirectory(service.DataPath);
    var state = new SavedState { HasOpenAiSnapshot = true, OriginalRegionLine = "AWS_REGION=us-east-2\nINJECTED=1" };
    File.WriteAllText(Path.Combine(service.DataPath, "state.json"), JsonSerializer.Serialize(state));
    Reject(() => service.Switch(false, "", "", ""));
    Assert(!File.Exists(service.EnvPath), "Invalid snapshot wrote environment.");
}));

Check("Malformed state JSON is rejected without changing existing settings", () => WithHome((home, service) =>
{
    string config = "model_provider = 'amazon-bedrock-runtime'\n";
    File.WriteAllText(service.ConfigPath, config); Directory.CreateDirectory(service.DataPath);
    File.WriteAllText(Path.Combine(service.DataPath, "state.json"), "{broken");
    Reject(() => service.Switch(false, "", "", ""));
    Assert(File.ReadAllText(service.ConfigPath) == config, "Malformed state changed provider.");
}));

int failed = 0;
int skipped = 0;
int fixtureIndex = Array.IndexOf(args, "--fixtures");
if (fixtureIndex >= 0 && fixtureIndex + 1 < args.Length)
{
    string fixtureRoot = args[fixtureIndex + 1]; Directory.CreateDirectory(fixtureRoot);
    Check("Generate 50 real switching fixtures for independent TOML verification", () =>
    {
        for (int i = 0; i < 50; i++)
            WithHome((home, service) =>
            {
                string newline = i % 2 == 0 ? "\n" : "\r\n";
                string[] providerLines = ["", "model_provider = 'openai' # provider", "\"model_provider\" = \"openai\"", "\"model_provi\\u0064er\" = 'openai'"];
                string config = "# fixture " + i + " café 🔄" + newline;
                if (providerLines[i % 4].Length > 0) config += providerLines[i % 4] + newline;
                if (i % 3 > 0) config += "model = 'gpt-6-sol' # model" + newline;
                if (i % 5 > 0) config += "service_tier = 'fast' # speed" + newline;
                config += "tools.api.url = 'https://example.invalid/'" + newline;
                config += "[features]" + newline + "fast_mode = true" + newline;
                config += "[tools.example]" + newline + "description = '''" + newline + "[fake]" + newline + "model_provider = 'fake'" + newline + "'''" + newline;
                config += "[[tools.catalog]]" + newline + "name = 'preserve'" + newline;
                string env = "UNRELATED=preserve" + newline;
                File.WriteAllText(service.ConfigPath, config); File.WriteAllText(service.EnvPath, env);
                string output = Path.Combine(fixtureRoot, i.ToString("D2")); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "original.toml"), config); File.WriteAllText(Path.Combine(output, "original.env"), env);
                service.Switch(true, "us-east-2", "default", "global.openai.gpt-6-sol");
                File.Copy(service.ConfigPath, Path.Combine(output, "bedrock.toml"), true); File.Copy(service.EnvPath, Path.Combine(output, "bedrock.env"), true);
                service.Switch(false, "", "", "");
                File.Copy(service.ConfigPath, Path.Combine(output, "restored.toml"), true); File.Copy(service.EnvPath, Path.Combine(output, "restored.env"), true);
            });
    });
}
var results = new List<object>();
foreach (var (name, test) in checks)
{
    try { test(); Console.WriteLine("PASS " + name); results.Add(new { name, status = "passed", error = (string?)null }); }
    catch (CheckSkipped ex) { skipped++; Console.WriteLine("SKIP " + name + ": " + ex.Message); results.Add(new { name, status = "skipped", error = ex.Message }); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); results.Add(new { name, status = "failed", error = ex.Message }); }
}
Console.WriteLine($"{checks.Count - failed - skipped}/{checks.Count} checks passed; {failed} failed; {skipped} skipped.");
int reportIndex = Array.IndexOf(args, "--report");
if (reportIndex >= 0 && reportIndex + 1 < args.Length)
    File.WriteAllText(args[reportIndex + 1], JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription, total = checks.Count, failed, skipped, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

sealed class CheckSkipped(string message) : Exception(message);
