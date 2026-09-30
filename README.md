# Provider Switch for Windows

[![Windows tests](https://github.com/Deepak8858/chatgpt-bedrock-switcher/actions/workflows/windows-tests.yml/badge.svg)](https://github.com/Deepak8858/chatgpt-bedrock-switcher/actions/workflows/windows-tests.yml)

Native Windows validation passed: **32 configuration checks, 50 independent TOML scenarios, and 8 GUI/tray smoke checks**, with no failures or skips. See the [Windows test results](docs/WINDOWS-TEST-RESULTS.md) for the run, reports, screenshot and tested download.

A small native Windows utility to switch **local ChatGPT Work / Codex** between your existing ChatGPT sign-in and **Amazon Bedrock Runtime**. It changes the shared local provider configuration. It does not send model requests itself, require an OpenAI API key, change your login, or stop your apps.

Version 1.1 preserves and restores the OpenAI `service_tier` preference as well as the model. Bedrock mode omits this setting because Bedrock supports on-demand inference, not the ChatGPT Fast tier. The utility displays the feature differences for the selected mode.

Version 1.2 fixes defects found in the expanded test suite: repeated-switch whitespace growth, unintended UTF-16 conversion, unsafe restoration from a corrupt saved snapshot, and partial switching when a file path is occupied by a directory. Failed writes roll back only files already committed. Invalid UTF-8/UTF-16 inputs are rejected; keep configuration files in UTF-8.

## Run

1. Extract `ProviderSwitch-windows-x64.zip` into a folder on your Windows 10/11 x64 PC.
2. Run `ProviderSwitch.exe`. No separate .NET installation or administrator access is required. This is an unsigned portable build.
3. In ChatGPT / Codex, sign in **with ChatGPT** to use your plan. If the apps are signed in with an OpenAI API key, selecting the OpenAI provider continues to use API billing.
4. Select **AWS Bedrock Runtime**, enter your existing AWS profile and source region, and optionally an inference profile ID. Leave Model blank to select an available model in the app. For example, the current documentation lists `global.openai.gpt-6-sol`; availability and permissions depend on your AWS account and region.
5. Click **Apply switch**. Finish active tasks, completely quit and reopen ChatGPT / Codex, and start a new **local Work or Codex task**. Check the provider in Codex CLI using `/status` where applicable.
6. Select **ChatGPT plan** and apply to return. Your previous OpenAI model selection is restored when the utility saved it.

Use **Minimize to tray** for convenient access. Right-click the tray icon to switch using the entered AWS settings. Closing the window exits the utility. Applying a switch does not restart apps or interrupt active tasks automatically.

## AWS setup

Use an existing AWS SDK profile with access to the supported OpenAI models in Bedrock Runtime. For an SSO profile, authenticate on your PC using `aws sso login --profile YOUR_PROFILE` before starting a new task. Your AWS administrator can help with model access, regions, permissions and quotas.

The utility writes `AWS_PROFILE`, `AWS_REGION` and `AWS_DEFAULT_REGION` to the `.codex/.env` file documented for desktop clients. It never opens AWS credential files. If `AWS_BEARER_TOKEN_BEDROCK` is already set in that file or the utility's environment, it shows a notice: the Bedrock API token takes precedence over the AWS SDK profile. The token is left in place. No secret-entry field is needed in this utility.

## What changes

The utility uses `%USERPROFILE%\.codex`, or an existing `CODEX_HOME` environment setting:

- `config.toml`: changes only root `model_provider`, `model` and `service_tier`, preserving other settings and comments. Bedrock Runtime uses `amazon-bedrock-runtime`; OpenAI uses your original `openai` setting or its implicit default. The OpenAI model and service-tier literals are saved and restored on return.
- `.env`: updates only the three non-secret AWS profile/region variables. Existing values are restored when returning to OpenAI if this utility captured them.
- `.provider-switch/state.json`: remembers provider/model literals and non-secret AWS settings.
- `.provider-switch/backups/config-*.toml`: keeps a byte-for-byte configuration backup before every applied switch. Protect these local backups as you protect your original configuration, which may contain private tool settings.

The utility never opens or changes `auth.json`. It preserves your ChatGPT sign-in. Changing between multiple ChatGPT accounts still uses the apps' own sign-in controls. It does not change a Windows-wide provider for other software.

## Supported scope

The current official [Amazon Bedrock setup guide](https://learn.chatgpt.com/docs/amazon-bedrock) documents the shared configuration for ChatGPT desktop local Work/Codex, Codex CLI, the IDE extension and SDK. Update your clients to versions supporting `amazon-bedrock-runtime`.

This configuration **does not reroute ordinary ChatGPT conversations or hosted ChatGPT/Codex cloud tasks to Bedrock**. Bedrock also has feature differences; see the official guide before relying on cloud-dependent capabilities.

### Paid plans and switching

Your paid subscription remains associated with your ChatGPT account. This utility does not change your subscription, workspace, authentication, tools or permission settings. When you return to OpenAI and use ChatGPT sign-in, the official apps determine access based on your actual plan, rollout, quotas and workspace policies. A local toggle cannot unlock or guarantee every paid feature across every plan, and this build has not been tested against live paid accounts.

| Capability | ChatGPT plan mode | Bedrock Runtime mode |
| --- | --- | --- |
| Billing for supported local Work/Codex inference | ChatGPT plan usage when signed in with ChatGPT | AWS Bedrock usage |
| Paid subscription features | According to your plan and official app availability | ChatGPT entitlements do not transfer to AWS |
| Supported local code tasks | According to your plan and client | Available with AWS access to a supported model |
| Fast mode | Available on supported models and plans; saved preference restored | Unavailable; saved service-tier preference omitted |
| Image generation/editing, voice dictation, web search | According to your plan and official app availability | Unavailable in the documented Bedrock integration |
| Codex cloud and cloud-dependent integrations | According to your plan and official app availability | Unavailable |

Switching is convenient from the window or tray menu, but **not a live switch inside an active task**. Restart the affected app or extension and start a new local task after changing providers. The utility does not force-close active work, migrate an existing task between providers, or silently fall back to OpenAI billing when AWS fails.

The utility refuses invalid TOML, duplicate managed AWS entries, selected root Codex profiles, other providers and redirected settings paths. CLI launch overrides and managed organization policies may override your local configuration. An existing `openai_base_url` override is preserved: if it routes requests through a proxy, review that setting before assuming direct OpenAI plan billing.

## Restore manually

Prefer selecting **ChatGPT plan** in the utility, which restores its saved model and AWS settings. To restore an earlier complete config, close the clients and copy the desired `.provider-switch/backups/config-*.toml` file over `config.toml`. Config backups do not include `.env` or authentication files. The saved non-secret original AWS lines are in `state.json` if manual restoration is needed.

## Build and verify

Requires .NET 8 SDK. From this directory:

```powershell
dotnet run --project Checks/ProviderSwitch.Checks.csproj -c Release
dotnet publish Desktop/ProviderSwitch.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64
```

The configuration checks run against isolated temporary directories and require no credentials or live inference. The [Windows CI workflow](https://github.com/Deepak8858/chatgpt-bedrock-switcher/actions/workflows/windows-tests.yml) builds and runs the configuration suite, independent parser checks, and native Windows form/tray smoke tests on Windows Server 2022. Open a completed workflow run to download its JSON reports, GUI screenshot, and tested Windows application. Live account billing and paid-plan entitlements require separate authenticated verification.

For the fuller automated suite and independent parser comparison:

```powershell
dotnet run --project Checks/ProviderSwitch.Checks.csproj -c Release -- --report artifacts/test-report.json --fixtures artifacts/fixtures
python Checks/verify-fixtures.py artifacts/fixtures
```

### Native Windows test kit

The separate `ProviderSwitch-v1.2-windows-test-kit.zip` includes the app, a self-contained configuration-check executable and `Run-Windows-Tests.cmd`. Extract it to a writable folder and run the command file. No .NET SDK, AWS credentials or ChatGPT login is required for those tests. It runs the same configuration suite and eight Windows form/tray smoke checks against temporary settings, then writes `configuration-test-report.json` and `windows-ui-test-report.json`.

Windows symbolic-link checks are explicitly marked skipped when Windows does not grant permission to create test links. Skips never count as passes. The UI smoke tests exercise provider selection, Apply, restoration, input errors, Refresh, tray menu commands and hiding/reopening the form. They do not exercise actual ChatGPT/Codex processes, OS credential stores or account billing.

Complete end-to-end verification still requires your actual Windows client versions and authenticated accounts: confirm ChatGPT plan access and its paid features, switch and restart the clients, confirm Bedrock identity/model access and AWS usage, then switch back and check the paid features again. The utility itself cannot prove paid-plan entitlements or emulate features missing from Bedrock.

## Third-party software

Includes the .NET runtime / Windows Desktop runtime (Microsoft, MIT license) and Tomlyn 0.19.0 (Alexandre Mutel, BSD 2-Clause license). See `THIRD-PARTY-NOTICES.txt` in the distribution.
