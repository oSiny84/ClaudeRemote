# ClaudeRemote

Remotely monitor and control the **Claude Code** desktop app on your Windows PC from your Android smartphone.

> **Currently supports Claude Code (Code mode) only.** Claude Chat / Cowork modes are not supported.

> ⚠️ **Claude App Version Compatibility**
> Commits made on or before **2026-04-15** only work with Claude Code app **versions below `1.2581.0 (f10398)`**.
> Starting from version `1.2581.0 (f10398)`, the Claude app UI was significantly changed and the UIAutomation mappings need to be reworked. Use a matching commit for your Claude app version.

[Korean (한국어)](README.ko.md)

## Overview

ClaudeRemote consists of two applications:

- **Windows Server** (C# WPF) — Controls the Claude Code app via UIAutomation and communicates with Android over WebSocket
- **Android Client** (Kotlin Compose) — Remotely view Claude output, send commands, and manage sessions/projects

Both devices must be on the same local network (Wi-Fi). This allows you to monitor Claude Code's progress and send commands from your phone, even when you're away from your desk.

## Download

Pre-built binaries are available in the [`Asset/`](Asset/) folder.

| File | Platform | Size | Requirements |
|------|----------|------|--------------|
| [ClaudeCodeRemote-1.0-Windows-x64.zip](Asset/ClaudeCodeRemote-1.0-Windows-x64.zip) | Windows x64 | ~3.0 MB | Windows 10/11 (x64), .NET 8 Desktop Runtime, Claude Code desktop app |
| [ClaudeCodeRemote-1.0-android.apk](Asset/ClaudeCodeRemote-1.0-android.apk) | Android | ~16.3 MB | Android 8.0 (API 26) or higher |

**Windows Setup:**
1. Download and extract the ZIP file
2. Install [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) if not already installed
3. Run `ClaudeRemote.Windows.exe`

**Android Setup:**
1. Download the APK file to your Android device
2. Allow installation from unknown sources in Settings → Security
3. Open the APK file to install

## Screenshots
### Server

![server](docs/screenshot/cShapServer.png)

### Android

<p align="left">
  <img src="docs/screenshot/AndChat.jpg" width="200">
  <img src="docs/screenshot/AndSession.jpg" width="200">
  <img src="docs/screenshot/AndSetting.jpg" width="200">
</p>

## Key Features

| Feature | Description |
|---------|-------------|
| Output Monitoring | View Claude output in Full / Latest / Summary modes |
| Remote Command Input | Type commands on Android and send them to Claude Code |
| Action Button Control | Tap Claude's choice buttons directly from Android |
| Session Management | List, switch, and create new sessions |
| Project Management | List and switch between projects |
| Real-time Status | Auto-detect Claude app launch/exit, streaming status |
| Auto Reconnect | Exponential backoff reconnection on network failure |
| Background Persistence | Android Foreground Service keeps connection alive in background |
| Notifications | Alerts for new output and connection loss |
| Quick Commands | One-tap buttons for Continue, Stop, New Chat |
| Command History | Recent 10 commands saved for quick reuse |
| Markdown Rendering | Basic rendering for code blocks, bold, italic, headings, lists |
| Theme | System / Dark / Light theme selection |

---

## Tech Stack

### Windows Server

| Item | Version |
|------|---------|
| Framework | .NET 8.0 (net8.0-windows) |
| UI | WPF (Windows Presentation Foundation) |
| Language | C# 12 |
| IDE | Visual Studio 2022 (v17.8+) |
| Architecture | MVVM |

#### NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| CommunityToolkit.Mvvm | 8.2.2 | MVVM helpers |
| Fleck | 1.2.0 | WebSocket server |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | DI container |
| Serilog | 3.1.1 | Logging |
| Serilog.Sinks.File | 5.0.0 | File logging |
| Serilog.Sinks.Console | 5.0.1 | Console logging |

### Android Client

| Item | Version |
|------|---------|
| Language | Kotlin 1.9.22 |
| UI | Jetpack Compose (BOM 2024.02.00) |
| Design | Material Design 3 |
| compileSdk | 34 (Android 14) |
| minSdk | 26 (Android 8.0) |
| targetSdk | 34 |
| JVM Target | 17 |
| IDE | Android Studio (latest stable) |

#### Key Libraries

| Library | Version | Purpose |
|---------|---------|---------|
| Compose Material3 | BOM 2024.02.00 | UI components |
| Navigation Compose | 2.7.7 | Screen navigation |
| Lifecycle ViewModel | 2.7.0 | ViewModel |
| OkHttp | 4.12.0 | WebSocket client |
| kotlinx-serialization-json | 1.6.3 | JSON serialization |
| kotlinx-coroutines-android | 1.8.0 | Async processing |
| DataStore Preferences | 1.0.0 | Settings persistence |

### Communication

| Item | Spec |
|------|------|
| Protocol | WebSocket (ws://) |
| Message Format | JSON (UTF-8) |
| Default Port | 8765 |
| Heartbeat | 30-second interval |
| Large Messages | Chunked at 8KB when exceeding 10KB |
| Protocol Version | v1.2 |

---

## Getting Started

### Prerequisites

- **Claude Code desktop app** must be installed on your Windows PC
- Windows PC and Android device must be on the **same Wi-Fi network**
- .NET 8 SDK (for Windows build)
- Android Studio (for Android build)

### 1. Build and Run Windows Server

```bash
# Navigate to solution directory
cd ClaudeRemote.Windows

# Restore packages and build
dotnet restore
dotnet build

# Run
dotnet run --project ClaudeRemote.Windows
```

Or open `ClaudeRemote.Windows.sln` in Visual Studio 2022 and press F5.

### 2. Build and Install Android App

Open the `ClaudeRemote.Android/` folder in Android Studio and build/install on a physical device or emulator.

### 3. Connect

1. **Windows**: Launch ClaudeRemote → Click "Connect Claude" → Claude Code app auto-detected
2. **Windows**: Click "Start Server" → Note the **IP address** shown in the header
3. **Android**: Launch ClaudeRemote → Enter the IP address and port (8765) from Windows → Tap "Connect"

---

## Usage

### Windows App

The Windows app acts as a **relay server** between the Claude Code app and the Android app.

| Area | Description |
|------|-------------|
| Header | Claude connection status (green/red), server status, connected clients, local IP |
| Controls | Claude connect, server start/stop, session management buttons |
| Sidebar | Session list, project list |
| Main Area | Real-time Claude output display |
| Log | Communication logs, event history |

#### Key Actions

1. **Connect Claude** — Auto-detects the Claude Code app process. Automatically switches to Code mode upon connection.
2. **Start Server** — Starts the WebSocket server. Default port is 8765 (configurable).
3. **Claude App Restart** — Automatically detected and reconnected (5-second monitoring interval).

### Android App

The Android app serves as a **remote control** for Claude Code.

#### Screens

**Chat Screen** (Main)
- Top: Claude output viewer (Full / Latest / Summary toggle)
- Middle: Markdown-rendered output content
- Bottom: Quick Commands (Continue, Stop, New) + command input field

**Manage Screen**
- Session list: view, select, add new sessions
- Project list: view, switch projects

#### Key Actions

1. **View Output** — Check Claude output in Full (complete), Latest (last response), or Summary mode
2. **Send Commands** — Type in the bottom input field and tap Send
3. **Quick Commands** — One-tap Continue, Stop, or New Session
4. **Action Buttons** — When Claude presents choices, buttons appear under "Claude is asking:" — tap to select
5. **Switch Sessions** — View and switch sessions in the Manage tab
6. **Switch Projects** — View and switch projects in the Manage tab
7. **Settings** — Tap the settings icon → Configure server address, auto-connect, notifications, theme

#### Connection Settings

| Setting | Description |
|---------|-------------|
| Server IP | Windows PC's local IP (shown in the Windows app header) |
| Port | WebSocket port (default 8765) |
| Auto-connect | Automatically connect to saved address on app launch |
| Emulator | Use `10.0.2.2` to access host PC |

---

## Project Structure

```
ClaudeRemote/
├── README.md                    # English documentation
├── README.ko.md                 # Korean documentation
├── docs/
│   ├── PM.md                    # PM role rules
│   ├── PM_Phases.md             # Phase management document
│   ├── WindowsProgrammer.md     # Windows development rules
│   ├── AndroidProgrammer.md     # Android development rules
│   ├── Windows_Tasks.md         # Windows task instructions
│   ├── Android_Tasks.md         # Android task instructions
│   ├── ClaudeUI_Map.md          # Claude app UI tree analysis
│   └── reports/                 # Phase work reports
│
├── protocol/
│   └── MessageProtocol.md       # WebSocket JSON protocol v1.2
│
├── ClaudeRemote.Windows/        # Windows WPF solution
│   ├── ClaudeRemote.Windows.sln
│   └── ClaudeRemote.Windows/
│       ├── Models/              # Data models
│       ├── Services/            # UIAutomation, WebSocket, session management
│       ├── ViewModels/          # MVVM ViewModels
│       └── Views/               # WPF XAML UI
│
└── ClaudeRemote.Android/        # Android Compose project
    └── app/src/main/java/com/clauderemote/
        ├── data/                # Models, WebSocket client, settings
        ├── service/             # Foreground Service
        ├── ui/                  # Compose screens, theme
        └── viewmodel/           # ViewModels
```

---

## Limitations

- **Claude Code Only** — Currently supports Claude Code (Code mode) only. Claude Chat and Cowork modes are not supported.
- **Same Network Required** — Windows PC and Android device must be on the same local network.
- **UIAutomation Dependency** — The Windows app automates Claude Code's UI elements. Updates to the Claude app that change the UI structure may cause instability.
- **Clipboard Usage** — Text input uses the system clipboard. Clipboard contents may be overwritten during input.
- **Unencrypted Communication** — WebSocket uses `ws://` (unencrypted). Do not use outside of a local network.

---

## License

MIT License
