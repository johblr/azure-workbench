# Azure Workbench

[![CI](https://github.com/johblr/azure-workbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/johblr/azure-workbench/actions/workflows/ci.yml)
[![Release](https://github.com/johblr/azure-workbench/actions/workflows/release.yml/badge.svg)](https://github.com/johblr/azure-workbench/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/johblr/azure-workbench?include_prereleases&sort=semver)](https://github.com/johblr/azure-workbench/releases)

A VS Code-style desktop workbench for Azure Service Bus (queues, topics, dead-letter replay) and App Service / Azure Functions (logs, configuration, deployment history). Runs on Windows, Linux and macOS.

## Download

Prebuilt, self-contained builds are on the repository's **Releases** page. You do **not** need .NET installed. Pick the file for your system:

| System | File |
| --- | --- |
| Windows, Intel/AMD | `AzureWorkbench-<version>-win-x64.zip` |
| Windows on Arm | `AzureWorkbench-<version>-win-arm64.zip` |
| macOS, Apple silicon (M1 and later) | `AzureWorkbench-<version>-osx-arm64.zip` |
| macOS, Intel | `AzureWorkbench-<version>-osx-x64.zip` |
| Linux, x86-64 | `AzureWorkbench-<version>-linux-x64.tar.gz` |
| Linux, Arm64 | `AzureWorkbench-<version>-linux-arm64.tar.gz` |

Each archive has a `README-FIRST.txt`, and `SHA256SUMS.txt` on the release lists checksums. Builds with a `-` in the version (for example `0.1.0-beta.1`) are marked as pre-releases.

Sign-in uses the Azure CLI (`az login` on the same OS as the app; Windows and WSL have separate CLI sessions) or the browser.

The builds are not code-signed, so expect a first-run warning:

- **Windows:** SmartScreen shows "Windows protected your PC". Choose **More info**, then **Run anyway**.
- **macOS:** Gatekeeper blocks the app the first time, because it isn't notarized by Apple. Dismiss the warning with **Done**, not **Move to Trash**, then:
  - **macOS 15 Sequoia and later:** open **System Settings → Privacy & Security**, scroll to **Security**, click **Open Anyway** next to "Azure Workbench", enter your password, then click **Open**. (Sequoia removed the old Control-click → Open shortcut.)
  - **macOS 14 Sonoma and earlier:** Control-click (right-click) **Azure Workbench.app** → **Open** → **Open**.
  - **Any version, from Terminal:** `xattr -dr com.apple.quarantine "/Applications/Azure Workbench.app"`

  This is needed once per downloaded version.
- **Linux:** remembered browser sign-in needs a keyring that provides libsecret (for example gnome-keyring); without one it is kept in memory for that run.

### Verify a download (optional)

Every release lists SHA-256 checksums in `SHA256SUMS.txt`:

```sh
sha256sum -c SHA256SUMS.txt --ignore-missing                 # Linux
shasum -a 256 -c SHA256SUMS.txt --ignore-missing             # macOS
Get-FileHash .\AzureWorkbench-<version>-win-x64.zip          # Windows (compare with SHA256SUMS.txt)
```

Releases also carry a GitHub **build provenance attestation**. This is a signed record, made by GitHub through Sigstore, that the exact file was built by this repository's Release workflow from a specific commit. With the [GitHub CLI](https://cli.github.com/):

```sh
gh attestation verify AzureWorkbench-<version>-<rid>.zip --repo johblr/azure-workbench
```

A passing check means the file is byte-for-byte what CI built. It does not make the build code-signed, so the first-run warnings above still apply. Attestations are free for public repositories; for a private repository they need GitHub Enterprise Cloud, and the release still works without them.

### Install on Linux

```sh
tar xzf AzureWorkbench-<version>-linux-x64.tar.gz
cd AzureWorkbench-<version>
./install.sh
```

`install.sh` installs for the current user only: the program in `~/.local/opt/azure-workbench`, a `azure-workbench` symlink in `~/.local/bin`, and the menu entry and icon under `~/.local/share/{applications,icons}`. It prints what it did and is safe to run again to upgrade. You can also just run `./AzureWorkbench` from the extracted folder.

## Build it yourself

You can build and run your own copy from source on Windows, macOS or Linux. No prebuilt download or code signing is involved, so there are no first-run warnings for builds you make on your own machine.

### 1. Install the prerequisites

| | Windows | macOS | Linux (Debian/Ubuntu shown) |
| --- | --- | --- | --- |
| .NET 10 SDK | `winget install Microsoft.DotNet.SDK.10` | `brew install --cask dotnet-sdk` | `sudo apt install dotnet-sdk-10.0` (Ubuntu 24.04+) or [Microsoft's install script](https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual) |
| Git | `winget install Git.Git` | `xcode-select --install` | `sudo apt install git` |
| Azure CLI (optional, for CLI sign-in) | `winget install Microsoft.AzureCLI` | `brew install azure-cli` | [Install the Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli-linux) |
| Python 3 (optional, only for `package.py`) | `winget install Python.Python.3.12` | preinstalled / `brew install python` | `sudo apt install python3` |

Check with `dotnet --version` (it should print `10.x`). On Linux the app also needs the usual desktop libraries (X11 or XWayland, fontconfig and ICU), which desktop installs already have. On a minimal system: `sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libicu74` (the ICU package name varies by release).

### 2. Get the code and run it

```sh
git clone https://github.com/johblr/azure-workbench.git
cd azure-workbench
dotnet run --project src/AzureWorkbench          # first run restores packages, then opens the app
```

Run the checks (no Azure access needed; they use fake responses):

```sh
dotnet run --project tests/AzureWorkbench.Checks
```

### 3. Build a standalone copy you can keep

`dotnet run` needs the SDK every time. To get a single executable that runs on its own (no .NET needed, so you can also copy it to colleagues on the same OS):

```sh
./publish.sh                 # Linux / macOS: detects linux-x64, linux-arm64, osx-x64 or osx-arm64
./publish.sh win-x64         # or cross-publish for another runtime
```

```powershell
./publish.ps1                # Windows (PowerShell 5+ or 7); -Runtime and -Output are optional
./publish.ps1 -Output C:\Apps\AzureWorkbench
```

Output goes to `artifacts/publish/<runtime>/`. The executable is `AzureWorkbench` (`AzureWorkbench.exe` on Windows). Close a running copy before publishing over it.

To get the same installable packages as the Releases page, including the macOS `.app` bundle and the Linux `install.sh` with a menu entry:

```sh
python3 packaging/package.py --rid osx-arm64 --version 0.1.0 --out artifacts/release   # macOS (Apple silicon)
python3 packaging/package.py --rid linux-x64 --version 0.1.0 --out artifacts/release   # Linux
python  packaging/package.py --rid win-x64   --version 0.1.0 --out artifacts/release   # Windows
```

Then install it:

- **macOS:** unzip and drag **Azure Workbench.app** to **Applications**.
- **Linux:** extract the `.tar.gz` and run `./install.sh` (see [Install on Linux](#install-on-linux)).
- **Windows:** unzip anywhere, e.g. `C:\Apps\AzureWorkbench`, and pin `AzureWorkbench.exe` to Start.

You can build any platform's package from Linux, including macOS (the .NET SDK ad-hoc signs it so it runs on Apple silicon). The Windows `.exe` icon is only embedded when you build on Windows.

### 4. Update later

```sh
git pull
./publish.sh            # or package.py again, then reinstall
```

### How the build is configured

Any publish with a runtime identifier (`-r <rid>`) is self-contained, single-file, ReadyToRun-compiled and compressed, with no `.pdb`. These settings live in `src/AzureWorkbench/AzureWorkbench.csproj`. It is deliberately **not** Native AOT or trimmed: the Azure SDK and reflection-based `System.Text.Json` are not trim-safe, so ReadyToRun and compression keep start-up time and download size reasonable instead (about 60 MB per platform).

## Cutting a release

Pushing a version tag builds every platform and creates a GitHub Release with generated notes, all archives and `SHA256SUMS.txt`:

```sh
git tag v0.1.0-beta.1 && git push origin v0.1.0-beta.1   # a '-' in the tag makes it a pre-release
git tag v0.1.0 && git push origin v0.1.0                  # stable release
```

The version is the tag without the leading `v`. To test the packaging without releasing, run the **Release** workflow manually (Actions, Run workflow, optionally set `version`); it uploads the archives as workflow artifacts and creates no release. Normal CI (`.github/workflows/ci.yml`) builds and runs the checks on Linux and Windows; a macOS job is optional and cannot fail the workflow.

### Windows Defender ASR exclusions

If you configured a Defender ASR-only exclusion for the old executable, update it in elevated PowerShell:

```powershell
Add-MpPreference -AttackSurfaceReductionOnlyExclusions 'C:\Apps\AzureWorkbench\AzureWorkbench.exe'
Remove-MpPreference -AttackSurfaceReductionOnlyExclusions 'C:\Apps\SbExplorer\SbExplorer.exe'
```

These are exact-file ASR exclusions, not antivirus exclusions. Only add the exception if you trust this build; anything replacing that file inherits the exception. Organisation-managed policy may override local settings.

## Explorer, quick open and pins

- The **Explorer** lists tenants → subscriptions → **Service Bus** namespaces (queues, topics and their subscriptions, with active · DLQ counts), **App Services** and **Function Apps**. Each app has **Logs**, **Configuration** and **Deployments** pages. Small coloured badges mark each resource type.
- After sign-in the app builds a **background index** of every queue, topic, subscription, App Service and Function App you can list. With Azure CLI it starts straight away for all tenants; with browser sign-in a tenant is indexed once you expand it, so you aren't prompted for tenants you don't use. The status bar shows progress (and how many subscriptions weren't accessible). Run **Refresh resource index** from the command palette after creating resources. Indexing only lists resources; it never reads messages or settings.
- **Search** (top of the explorer) shows fuzzy **MATCHES** from the whole index, and also narrows the tree to matching loaded nodes. Click a match to open it. **Esc** clears it. **Collapse** folds everything.
- **Ctrl+P** / **Cmd+P** opens quick open over every indexed, loaded and pinned resource, with fuzzy matching (e.g. `ordq` finds `orders-queue`). **Ctrl+Shift+P** / **Cmd+Shift+P**, or typing `>`, shows commands (sign in, split/move editors, toggle sidebar, close all tabs, forget browser sign-ins). **Ctrl+B** / **Cmd+B** toggles the sidebar.
- Right-click a queue, topic, subscription or app page and choose **Pin**. Pins appear under **Pinned** and in quick open, survive restarts, and are resolved on demand after sign-in (you still need access to them).
- Right-click any loaded folder and choose **Refresh** to reload it.
- Settings (pins and the last sign-in method) are stored in `%APPDATA%\AzureWorkbench` on Windows and `~/.config/AzureWorkbench` on Linux and macOS (override with `AZUREWORKBENCH_HOME`). They contain resource IDs, never secrets.

### Several tenants with Azure CLI

`az login` signs you in to your home tenant. Other tenants often need their own sign-in, especially when they require MFA. When a tenant can't be opened, the explorer shows `Run: az login --tenant <id>` under it; right-click the tenant and choose **Copy 'az login' command for this tenant**. Run that command in a terminal, then right-click the tenant and choose **Refresh**. CLI tokens are cached per tenant and requested a few at a time, so indexing many tenants doesn't flood your machine with `az` processes.

### Staying signed in

The app signs in automatically at start-up with the method you last used, when that needs no prompt: Azure CLI, or a browser account it remembers. Browser tokens are kept in the OS credential store (Windows DPAPI, macOS Keychain, Linux libsecret), and a non-secret account record lets later runs sign in silently. Where no keyring is available (for example WSL or a minimal Linux desktop), browser sign-in falls back to memory for that run and the status bar says so. Install a keyring such as gnome-keyring, or run the opt-in command **Remember browser sign-in without a keyring (unencrypted)**, which stores tokens in an unencrypted file in your profile folder (protected only by file permissions). Run it again to turn it off. Use the **Forget remembered browser sign-ins** command to choose a different account.

## Dockable tabs and editor groups

Select a queue or app in the explorer to open its own tab. Selecting an already-open resource focuses its existing tab. Tabs retain their messages, selection, filters and live stream when hidden.

- Drag a tab along its tab strip to reorder it. Drag it into another group's tab strip to move it, or onto an editor edge to split. Docking indicators show the drop target.
- Use **Split right** / **Split down** on the active tab for side-by-side or stacked editors. Open at least two tabs first. Splits can be nested.
- Use **Move left** / **Move right** to move between groups in layout order. Drag dividers to resize groups. Empty groups disappear.
- **Ctrl+Tab** / **Ctrl+Shift+Tab** cycle tabs in the current group, **Ctrl+W** closes the active tab, **Ctrl+\\** splits right and **Ctrl+Shift+\\** splits down. On macOS, **Cmd** works in place of Ctrl for everything except Ctrl+Tab.
- Multiple queue and log tabs can operate independently across tenants. Queue actions always target the resource fixed to that tab. A busy queue disables only its own controls, not other tabs or log streams.
- Close a tab with **×**, middle-click, or the tab's right-click menu (including close other/all). All close paths enforce the same safeguards. Closing a log tab stops its stream; switching tabs or moving it does not. Busy queue tabs cannot close until their operation finishes.
- Received-and-deleted messages belong to their queue tab. They are retained when switching/moving tabs; closing that tab or the window prompts before discarding them. Resending received messages removes the resent entries from the in-memory list. A new peek/receive is blocked while that tab still holds received-and-deleted messages.
- Configuration tabs with unsaved edits are guarded the same way: closing the tab or window asks before discarding them.
- Right-click an explorer node and choose **Refresh** to reload its children (for example after creating a queue or app).
- Signing in again explicitly closes all tabs, stops streams and resets connections/credentials. The confirmation warns about discarding received messages; active queue operations must finish first.

The workspace uses [Dock for Avalonia](https://github.com/wieslawsoltes/Dock), licensed under MIT (see [ThirdPartyNotices.txt](src/AzureWorkbench/ThirdPartyNotices.txt), also included in published output), rather than a custom tab engine or Electron. Floating windows are disabled; all groups stay inside the app. Tabs and split layouts are not saved across app restarts.

## Service Bus: queues, topics and subscriptions

Select a queue, topic or subscription to open its tab. **Queues** and **subscriptions** support everything below. **Topics** hold no messages, so their tab only sends (composer and **Import & send...**) and shows counts. Sends from a **subscription** tab go to its topic, so every subscription whose rules match receives them; the tab warns about this.

### Viewing messages

- Choose **Active** or **Dead-letter** and a count, then **Peek**. **Peek next** continues from the last sequence number, so you can page through a large dead-letter queue.
- **Filter messages** (**Ctrl+F** / **Cmd+F**, **Esc** clears) matches message ID, subject, correlation ID, dead-letter reason and description, body and custom properties, optionally as a **Regex**. The **reason** picker narrows to one dead-letter reason. **Select all shown** selects the visible messages. Hidden messages are always deselected, so actions only touch what you can see.
- **DETAILS** shows the selected message. **Copy body** and **Copy message JSON** copy it. **Find in logs** appears when the message carries a `traceparent` / `Diagnostic-Id` or a correlation ID. It offers your open Logs tabs and every indexed app (Function Apps first). Choosing one opens its Logs tab if needed, selects its linked Application Insights and searches the last 24 hours for that operation across traces, exceptions, requests and dependencies.

### Replaying and moving dead-letters

- **Resend to...** sends copies of the selected messages to this entity or any other indexed queue or topic, including ones in other namespaces. Source messages are unchanged.
- **Move to...** (dead-letter only) sends each selected message to the target and then removes it from the dead-letter queue. It sends before removing, so the worst case is a duplicate, never a loss.
- **Edit as new...** copies one message's body, content type, subject and properties into the composer so you can fix it before sending. That creates a new message. Use **Complete selected** to remove the original dead-letter once you're happy.
- **Complete selected** (dead-letter only) removes the selected dead-lettered messages without resending them, after confirmation.
- **Purge DLQ...** removes every dead-lettered message, in batches, after you type the entity name. It shows progress and can be cancelled. It never touches active messages.
- **Receive & delete...** removes messages from Azure and holds them only in the tab (see the safeguards below).
- **Export selected...** saves messages to a JSON file (body, metadata, properties, dead-letter reason). **Import & send...** sends a file's messages to this entity, keeping their original message IDs so duplicate detection can apply.

### Sending a new message

Expand **New message**. Enter the body, content type (defaults to `application/json`), optional subject, optional custom properties as a JSON object, and optionally **Send at** (a future local date/time for a scheduled message). Then click **Send message**.

- JSON content types validate the body before sending; choose `text/plain` for unstructured text. Custom property values support strings, booleans and numbers, not objects, arrays or null.
- Sends always target the tab's entity (the topic, for subscription tabs), even if **Dead-letter** is selected. This creates a real message and can trigger consumers. Each send gets a new message ID. Scheduled sends report the scheduled time and sequence number.
- Success clears the body and collapses the composer, keeping the other fields for the next message. Failures keep the draft. If delivery wasn't confirmed, check Azure before retrying to avoid duplicates.
- Drafts survive tab switches and docking moves, but are not saved when the tab or app closes.

## Activity feedback

Queue actions show an animated progress bar and operation-specific status (sending, moving, peeking, etc.). Conflicting controls in that tab are disabled until completion or failure; duplicate clicks are ignored. Other tabs and live streams remain usable. Manual count refresh disables its own button and shows **Refreshing counts...** without locking message actions.

Sign-in and other shell operations show progress and temporarily disable navigation. Log discovery and recent queries show progress and disable conflicting inputs; **Cancel query** remains available. Live tails show activity with **Stop** available without blocking recent queries.

## App configuration (environment variables)

Expand an app under **App Services** or **Function Apps** and select **Configuration**. (Selecting the app itself, or **Logs**, opens its log tab.) The tab shows the production app's **Application settings** (exposed to the app as environment variables) and **Connection strings**.

- Values are masked until you tick **Show values**. **Filter** matches names, and values while they are shown.
- Edit names and values in place, choose a connection string **Type**, tick **Slot** for deployment slot settings (they stay with the slot during swaps), remove rows with **×** and create rows with **Add**. New rows are marked green and modified rows blue.
- **Advanced edit (JSON)** switches both lists to the same JSON format as the Azure portal's advanced edit, e.g. `[{"name": "KEY", "value": "...", "slotSetting": false}]` (connection strings add `"type"`). Invalid JSON is reported and never half-applied.
- **Save changes...** (**Ctrl+S** / **Cmd+S**) lists every added, changed and removed name (never values) plus any warnings, and asks for confirmation. Saving application settings or connection strings **restarts the app**; changing only slot flags does not.
- Before writing, the app re-reads the configuration from Azure. If someone else changed it since you loaded it, the save is refused so you don't overwrite their change: **Reload** and reapply your edits. Only sections you changed are written, and other slot-sticky names (such as storage mounts) are preserved.
- Names are validated: blank, duplicate (case-insensitive) and `=`-containing names block saving. On Linux apps, names with characters other than letters, digits, `_`, `.`, `:` and `-` produce a warning because they may not reach the app as environment variables.
- **Discard changes** restores the last loaded values. Nothing is stored locally.

Reading values requires `Microsoft.Web/sites/config/list/action` and saving requires write access on the app (for example Website Contributor or Contributor). Reader is not enough. Key Vault references (`@Microsoft.KeyVault(...)`) are shown and saved as their reference text.

## Deployment history

Select an app's **Deployments** page to list its recorded deployments, newest first: status, start time, duration, author, deployer (e.g. GitHub Actions, ZipDeploy) and message, with the **ACTIVE** deployment marked. Select one to see its full details and deployment log, including the detailed build/deploy output (e.g. Oryx) that Kudu keeps under individual steps (up to 20 per deployment, fetched only from the app's public `*.scm.azurewebsites.net` endpoint). **Copy details** copies everything shown. The page is read-only (no redeploy). It needs Reader access; Flex Consumption and some container deployments record little or no history here.

## Compare and copy configuration

With two apps' **Configuration** tabs open, choose **Compare with...** in one of them. A **COMPARE** tab lists settings and connection strings that differ, exist in only one app, or (with **Hide identical** off) match. Values stay masked unless **Show values** is ticked. Tick rows and click **Copy selected into this app** to bring the other app's values into your edits; nothing is saved until you use **Save changes...** with its usual confirmation. The comparison uses the other app's saved configuration and this tab's current edits. **Export .env** copies the current settings as `KEY=value` lines (after a reminder that values are secrets).

## App logs

Expand a subscription's **App Services** or **Function Apps** folder and select an app. Production apps are listed; deployment slots are not included.

- **Recent / Application Insights:** select the app's Insights component, then **Refresh recent**. Choose which telemetry to include: **Traces** and **Exceptions** by default, plus **Requests** (successful or failed function/HTTP executions, with result code and duration) and **Dependencies** (outgoing calls such as Service Bus, SQL and HTTP). Enter an **Operation ID** to follow one operation end to end; each line shows its item type and operation ID. The app uses the resource link tag when present; otherwise select explicitly from the subscription's components. Linked components in other subscriptions are fetched if accessible. Unlinked cross-subscription components aren't listed. Set the time window (1–1440 minutes), minimum severity, message search and optional `cloud_RoleName`. Shared components contain multiple apps: without a role filter, results are component-wide. This queries traces and exceptions, not every telemetry table, and returns at most 500 entries. Telemetry ingestion and sampling can delay or omit entries. Recent logs refresh manually.
- **Live tail / Kudu:** **Start live tail**, **Stop**, **Follow output** and **Clear**. Application/file logging must already be enabled. The app does not enable logging or retrieve publishing passwords. It uses your Entra identity against SCM. SCM access restrictions, networking and publishing permissions still apply. The stream can represent only one instance; Linux Consumption and Flex Consumption may not offer this endpoint. Use Azure's Application Insights Live Metrics for supported cross-instance live monitoring.
- **Filter and level (Ctrl+F / Cmd+F):** the bar above both log tabs filters what is displayed by minimum level (**All levels**, **Information+**, **Warning+**, **Error+**, **Critical**) and by text (case-insensitive) or **Regex**. **Esc** clears the text, and **Clear filter** resets everything. It filters output you already have, so it doesn't run a new query or change any logging settings in Azure. For recent logs, the level comes from Application Insights severity. For the live tail, the level is detected from each line's text (`Information`, `[Warning]`, `fail:`, `ERROR` and similar). Lines without a level, such as stack traces, belong to the line above, so filters keep whole entries together. The bar shows how many lines or entries match. The query's own **level** setting on the Recent tab still limits what Application Insights returns.
- **Copy and paste:** select output with the mouse and press **Ctrl+C** / **Cmd+C**, or right-click for **Copy selection**, **Select all** and **Copy all visible output**. **Copy visible** copies the current tab's filtered output. While you have live-tail text selected, the display pauses so new output doesn't clear your selection. Click the output to resume. Buffering continues in the meantime. In configuration tabs, tick **Show values** to make values copyable, or use **Advanced edit (JSON)** to copy or paste all settings at once.
- Each app tab owns its requests and stream. Closing the tab, signing in again or closing the app cancels them; changing the active tab does not. Live display updates are batched four times a second and keep only the latest 200,000 characters; recent output keeps the first 200,000 characters of the newest-first results. Output truncation is labelled. No log files are saved locally.
- Missing Insights resources, failed queries, denied access and unsupported streams are displayed explicitly. A quiet stream may mean the app is idle or logging is disabled. Start again after a stream disconnects.

Only Azure public-cloud endpoints are supported. Recent logs require read/query access to the selected Application Insights resource (for example Monitoring Reader, subject to resource/workspace access mode). SCM streaming requires the appropriate App Service publishing permissions (for example Website Contributor). Service Bus Data Owner does not grant log access.

Service Bus counts refresh every 10 seconds for visible expanded namespaces and all open queue tabs. Messages are manual snapshots. **Receive & delete** removes messages from Azure; closing its tab or the app discards any unsent received messages after confirmation.

## Checks

The checks use fake HTTP responses and credentials, never Azure. Workspace interactions run through Avalonia's headless test backend:

```sh
dotnet run --project tests/AzureWorkbench.Checks
dotnet build AzureWorkbench.slnx
```

They cover configuration diffing/validation/JSON round-trips, configuration reads and minimal writes with conflict detection, masked values and unsaved-change guards, discovery/paging, token destinations, query escaping and parsing, explicit errors, output bounds, stream cancellation, tab state retention, real pointer tab reordering/docking, horizontal/vertical splits, divider resizing, keyboard/middle-click actions, queue state isolation, message composition/validation, mocked sends, draft retention on failure, duplicate-click prevention, progress/control restoration and guards against discarding received messages.

## License

[MIT](LICENSE). Third-party components and their licences are listed in [ThirdPartyNotices.txt](src/AzureWorkbench/ThirdPartyNotices.txt), which is included in every download.
