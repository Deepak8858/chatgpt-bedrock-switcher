using System.Diagnostics;
using System.Text.Json;
using ProviderSwitch.Core;

namespace ProviderSwitch.Desktop;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        int smokeIndex = Array.IndexOf(args, "--ui-smoke-test");
        if (smokeIndex >= 0)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var reportPath = smokeIndex + 1 < args.Length ? Path.GetFullPath(args[smokeIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "windows-ui-test-report.json");
            RunUiSmokeTest(reportPath);
            return;
        }
        using var singleInstance = new Mutex(true, "Local\\ProviderSwitch.Windows", out bool created);
        if (!created) { MessageBox.Show("Provider Switch is already running. Check your system tray.", "Provider Switch"); return; }
        Application.Run(new SwitchWindow());
    }

    private static void RunUiSmokeTest(string reportPath)
    {
        string home = Path.Combine(Path.GetTempPath(), "provider-switch-ui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var results = new List<UiResult>();
        try
        {
            var service = new SwitchService(home);
            File.WriteAllText(service.ConfigPath, "model = 'gpt-6-sol'\nservice_tier = 'fast'\n");
            using var window = new SwitchWindow(service);
            window.Shown += (_, _) => window.BeginInvoke(new Action(() =>
            {
                try
                {
                    results.AddRange(window.RunUiChecks());
                    using var screenshot = new Bitmap(window.Width, window.Height);
                    window.DrawToBitmap(screenshot, new Rectangle(Point.Empty, window.Size));
                    screenshot.Save(Path.ChangeExtension(reportPath, ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception ex) { results.Add(new("UI smoke test execution", false, ex.Message)); }
                finally { window.Close(); }
            }));
            Application.Run(window);
        }
        catch (Exception ex) { results.Add(new("Windows form startup", false, ex.Message)); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow,
                platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                isolatedSettings = true,
                total = results.Count,
                failed = results.Count(r => !r.Passed),
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Delete(home, true);
        }
        Environment.ExitCode = results.Count > 0 && results.All(r => r.Passed) ? 0 : 1;
    }
}

internal record UiResult(string Name, bool Passed, string? Error);

internal sealed class SwitchWindow : Form
{
    private readonly SwitchService service;
    private readonly RadioButton openai = new() { Text = "ChatGPT plan", AutoSize = true, Checked = true };
    private readonly RadioButton bedrock = new() { Text = "AWS Bedrock Runtime", AutoSize = true };
    private readonly TextBox region = new();
    private readonly TextBox profile = new();
    private readonly TextBox model = new();
    private readonly Label current = new() { AutoSize = true };
    private readonly Label feedback = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Label tokenNotice = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(150, 95, 0) };
    private readonly Label availability = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Button apply = new() { Text = "Apply switch", AutoSize = true, Padding = new Padding(12, 7, 12, 7) };
    private readonly Panel awsFields = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu;
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true, Padding = new Padding(8, 7, 8, 7) };
    private bool loading;

    public SwitchWindow() : this(new SwitchService(SwitchService.DefaultHome)) { }
    internal SwitchWindow(SwitchService service)
    {
        this.service = service;
        Text = "Provider Switch";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(248, 249, 252);
        ForeColor = Color.FromArgb(30, 39, 54);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(640, 680);
        ClientSize = new Size(680, 740);
        Icon = SystemIcons.Application;

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, AutoScroll = true };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(outer);
        void Add(Control control) { control.Margin = new Padding(0, 0, 0, 16); outer.Controls.Add(control); }
        Add(new Label { Text = "Choose who powers your work", Font = new Font("Segoe UI", 19, FontStyle.Bold), AutoSize = true });
        Add(new Label { Text = "Switch local ChatGPT Work and Codex between your ChatGPT plan and AWS billing.", AutoSize = true, MaximumSize = new Size(580, 0) });
        Add(current);

        var choices = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        openai.Padding = new Padding(10); bedrock.Padding = new Padding(10);
        openai.Margin = new Padding(0, 0, 14, 0);
        choices.Controls.AddRange([openai, bedrock]);
        Add(choices);
        Add(new Label { Text = "For plan usage, sign in to your apps with ChatGPT. An OpenAI API-key login still uses API billing. Change accounts through the apps' sign-in controls.", AutoSize = true, MaximumSize = new Size(560, 0) });
        Add(availability);

        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Field(string name, TextBox input, string hint)
        {
            var label = new Label { Text = name, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 3, 0, 3);
            fields.Controls.Add(label); fields.Controls.Add(input);
            var help = new Label { Text = hint, AutoSize = true, MaximumSize = new Size(390, 0), ForeColor = Color.FromArgb(92, 102, 117), Margin = new Padding(0, 0, 0, 12) };
            fields.Controls.Add(new Label { AutoSize = true }); fields.Controls.Add(help);
        }
        Field("AWS region", region, "Use a source region where your Bedrock model is available.");
        Field("AWS profile", profile, "An existing profile in your AWS configuration, e.g. default or work-sso.");
        Field("Model (optional)", model, "Bedrock inference profile ID. Leave blank to choose a model in the app after switching.");
        awsFields.Controls.Add(fields); Add(awsFields); Add(tokenNotice);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var minimize = new Button { Text = "Minimize to tray", AutoSize = true, Padding = new Padding(8, 7, 8, 7) };
        actions.Controls.AddRange([apply, refresh, minimize]); Add(actions);
        Add(feedback);
        Add(new Label { Text = "After switching: finish active tasks, fully quit and reopen ChatGPT / Codex, then start a new local Work or Codex task. Ordinary ChatGPT chats and cloud tasks don't use this setting.", AutoSize = true, MaximumSize = new Size(560, 0) });

        var links = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var docs = new LinkLabel { Text = "Official setup guide", AutoSize = true, Margin = new Padding(0, 0, 18, 0) };
        var folder = new LinkLabel { Text = "Open settings folder", AutoSize = true };
        links.Controls.AddRange([docs, folder]); Add(links);
        Add(new Label { Text = "Settings: " + service.ConfigPath, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(92, 102, 117) });

        var menu = new ContextMenuStrip();
        trayMenu = menu;
        menu.Items.Add("Show Provider Switch", null, (_, _) => ShowWindow());
        menu.Items.Add("Use ChatGPT plan", null, (_, _) => { openai.Checked = true; Apply(); });
        menu.Items.Add("Use AWS Bedrock Runtime", null, (_, _) => { bedrock.Checked = true; Apply(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = Icon, Text = "Provider Switch", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        FormClosed += (_, _) => tray.Dispose();
        bedrock.CheckedChanged += (_, _) => { if (!loading) { awsFields.Enabled = bedrock.Checked; UpdateAvailability(); } };
        apply.Click += (_, _) => Apply();
        refresh.Click += (_, _) => Reload();
        minimize.Click += (_, _) => { Hide(); tray.ShowBalloonTip(3500, "Provider Switch", "Right-click the tray icon to switch providers.", ToolTipIcon.Info); };
        docs.LinkClicked += (_, _) => Open("https://learn.chatgpt.com/docs/amazon-bedrock");
        folder.LinkClicked += (_, _) => { Directory.CreateDirectory(service.Home); Open(service.Home); };
        Shown += (_, _) => Reload();
        AcceptButton = apply;
    }

    internal List<UiResult> RunUiChecks()
    {
        var results = new List<UiResult>();
        void Verify(string name, Action action)
        {
            try { action(); results.Add(new(name, true, null)); }
            catch (Exception ex) { results.Add(new(name, false, ex.Message)); }
        }
        void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Verify("Window renders and displays configured OpenAI provider", () =>
        {
            Assert(Visible && IsHandleCreated && current.Text.Contains("ChatGPT / OpenAI"), "Initial form did not load.");
            Assert(openai.Checked && !awsFields.Enabled, "Initial provider controls are incorrect.");
        });
        Verify("Selecting Bedrock enables AWS fields and shows feature limits", () =>
        {
            bedrock.Checked = true;
            Assert(awsFields.Enabled && availability.Text.Contains("aren't available"), "Provider selection did not update controls.");
        });
        Verify("Apply button saves Bedrock provider and omits Fast tier", () =>
        {
            region.Text = "us-east-2"; profile.Text = "default"; model.Text = "global.openai.gpt-6-sol";
            apply.PerformClick();
            var status = service.Inspect();
            Assert(status.Provider == "amazon-bedrock-runtime" && status.Model == "global.openai.gpt-6-sol", "Apply did not save Bedrock.");
            Assert(Configuration.Literal(File.ReadAllText(service.ConfigPath), "service_tier") == null, "Fast tier was sent to Bedrock.");
            Assert(feedback.Text.StartsWith("Saved."), "Restart guidance was not displayed.");
        });
        Verify("OpenAI selection restores model and paid speed preference", () =>
        {
            openai.Checked = true; apply.PerformClick();
            Assert(service.Inspect().Provider == "openai" && service.Inspect().Model == "gpt-6-sol", "OpenAI model not restored.");
            Assert(Configuration.String(File.ReadAllText(service.ConfigPath), "service_tier") == "fast", "Fast preference not restored.");
            Assert(!awsFields.Enabled, "AWS controls remained enabled in OpenAI mode.");
        });
        Verify("Invalid input shows an error and leaves provider unchanged", () =>
        {
            bedrock.Checked = true; region.Text = "invalid"; apply.PerformClick();
            Assert(service.Inspect().Provider == "openai", "Invalid input changed settings.");
            Assert(feedback.ForeColor == Color.Firebrick && feedback.Text.Contains("region"), "Input error was not shown.");
        });
        Verify("Refresh reloads saved provider and AWS settings", () =>
        {
            refresh.PerformClick();
            Assert(openai.Checked && region.Text == "us-east-2", "Refresh did not load saved settings.");
        });
        Verify("Tray menu applies both provider switches", () =>
        {
            trayMenu.Items[2].PerformClick();
            Assert(service.Inspect().Provider == "amazon-bedrock-runtime", "Tray Bedrock switch failed.");
            trayMenu.Items[1].PerformClick();
            Assert(service.Inspect().Provider == "openai", "Tray OpenAI switch failed.");
        });
        Verify("Tray window can be hidden and shown again", () =>
        {
            Hide(); Assert(!Visible && tray.Visible, "Tray icon unavailable while hidden.");
            ShowWindow(); Assert(Visible && WindowState == FormWindowState.Normal, "Window did not reopen.");
        });
        return results;
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { MessageBox.Show("Could not open: " + target, "Provider Switch"); }
    }
    private void Reload()
    {
        loading = true;
        try
        {
            var status = service.Inspect();
            current.Text = "Configured provider: " + (status.Provider == "openai" ? "ChatGPT / OpenAI" : status.Provider);
            current.Font = new Font(Font, FontStyle.Bold);
            bedrock.Checked = status.Provider == "amazon-bedrock-runtime";
            openai.Checked = !bedrock.Checked;
            region.Text = status.State.Region; profile.Text = status.State.Profile; model.Text = status.State.BedrockModel;
            awsFields.Enabled = bedrock.Checked;
            UpdateAvailability();
            tokenNotice.Text = status.HasAwsToken ? "A Bedrock API token is configured. It takes priority over the selected AWS profile. This utility leaves that token in place." : "AWS credentials stay in your existing AWS profile. For SSO, sign in with AWS before using Bedrock.";
            apply.Enabled = true;
            feedback.Text = "";
        }
        catch (Exception ex) { feedback.Text = ex.Message; feedback.ForeColor = Color.Firebrick; apply.Enabled = false; }
        finally { loading = false; }
    }
    private void UpdateAvailability()
    {
        availability.Text = bedrock.Checked
            ? "Bedrock provides supported local Work / Codex features. Fast mode, image generation, voice dictation, web search and Codex cloud aren't available. ChatGPT subscription features don't transfer to AWS."
            : "Your ChatGPT account keeps the features available to its paid plan, subject to normal limits and workspace policies. Your saved model and speed preferences are restored when switching back.";
        availability.ForeColor = bedrock.Checked ? Color.FromArgb(150, 95, 0) : Color.FromArgb(26, 108, 64);
    }
    private void Apply()
    {
        try
        {
            service.Switch(bedrock.Checked, region.Text, profile.Text, model.Text);
            Reload();
            feedback.Text = "Saved. Restart ChatGPT / Codex and start a new local task to use this provider.";
            feedback.ForeColor = Color.FromArgb(26, 108, 64);
            if (!Visible) tray.ShowBalloonTip(5000, "Provider saved", feedback.Text, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            feedback.Text = ex.Message; feedback.ForeColor = Color.Firebrick;
            if (!Visible) { ShowWindow(); MessageBox.Show(this, ex.Message, "Switch could not be applied", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
