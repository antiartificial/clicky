# Clicky - Agent Instructions

<!-- This is the single source of truth for all AI coding agents. CLAUDE.md is a symlink to this file. -->
<!-- AGENTS.md spec: https://github.com/agentsmd/agents.md — supported by Claude Code, Cursor, Copilot, Gemini CLI, and others. -->

## Overview

macOS menu bar companion app. Lives entirely in the macOS status bar (no dock icon, no main window). Clicking the menu bar icon opens a custom floating panel with companion voice controls. Uses push-to-talk (ctrl+option) to capture voice input, transcribes it via AssemblyAI streaming, and sends the transcript + a screenshot of the user's screen to Claude. Claude responds with text (streamed via SSE) and voice (ElevenLabs TTS). A blue cursor overlay can fly to and point at UI elements Claude references on any connected monitor.

For the macOS app, all API keys live on a Cloudflare Worker proxy; nothing sensitive ships in the app. The separate Windows prototype may instead store direct Anthropic or OpenAI keys in Windows Credential Manager, as described below.

The repository also contains `windows/`, a separate .NET 10 WPF Clicky prototype. It captures the foreground application before accepting a typed or foot-pedal voice question, then uses the active-window title and screenshot to guide one visible action at a time in any desktop app. Adobe apps, Visual Studio, and Rive are examples, not explicit integrations. It supports the existing Worker `/chat` route plus direct Anthropic and OpenAI modes, local `System.Speech` dictation, a configurable global F13-F24 pedal key, terminal `POINT` parsing, and a click-through non-activating cue. It has no TTS/audio playback, persisted non-secret settings, installer, or live API E2E verification yet.

## Architecture

- **App Type**: Menu bar-only (`LSUIElement=true`), no dock icon or main window
- **Framework**: SwiftUI (macOS native) with AppKit bridging for menu bar panel and cursor overlay
- **Pattern**: MVVM with `@StateObject` / `@Published` state management
- **AI Chat**: Claude (Sonnet 4.6 default, Opus 4.6 optional) via Cloudflare Worker proxy with SSE streaming
- **Speech-to-Text**: AssemblyAI real-time streaming (`u3-rt-pro` model) via websocket, with OpenAI and Apple Speech as fallbacks
- **Text-to-Speech**: ElevenLabs (`eleven_flash_v2_5` model) via Cloudflare Worker proxy
- **Screen Capture**: ScreenCaptureKit (macOS 14.2+), multi-monitor support
- **Voice Input**: Push-to-talk via `AVAudioEngine` + pluggable transcription-provider layer. System-wide keyboard shortcut via listen-only CGEvent tap.
- **Element Pointing**: Claude embeds `[POINT:x,y:label:screenN]` tags in responses. The overlay parses these, maps coordinates to the correct monitor, and animates the blue cursor along a bezier arc to the target.
- **Concurrency**: `@MainActor` isolation, async/await throughout
- **Analytics**: PostHog via `ClickyAnalytics.swift`

### Windows Prototype

- **App Type**: `net10.0-windows` WPF tool window with a Windows Forms `NotifyIcon` tray shell; per-monitor V2 DPI aware.
- **Pattern**: MVVM-style view model plus `CompanionSessionCoordinator` state machine. `App.xaml.cs` performs explicit startup composition.
- **Focus-safe capture**: The compact companion is non-activating. `TutorInteractionService.PrepareAsync` captures the current foreground window before `CompanionWindow` activates and focuses the typed-question box.
- **Screen Capture**: `ActiveWindowCaptureService` snapshots the foreground HWND/bounds/title, rejects sources above 40 million pixels, then performs GDI copy and JPEG work off the UI thread. The default-on optimization leaves captures at or below 1920x1080 at full size; when either dimension exceeds that threshold, it encodes both dimensions at half scale with JPEG quality 88 while preserving the original physical bounds for point mapping. There is no full-display, multi-display, or occlusion-independent capture path yet.
- **AI Chat**: `ProviderRoutingChatClient` snapshots and routes each request to the configured Worker, direct Anthropic Messages API, or direct OpenAI Responses API client. `VisualGuideTutor` supplies the generic screenshot-grounded prompt and up to 10 conversation turns are retained in memory.
- **Voice Input**: A global low-level keyboard monitor listens for the configurable F13-F24 foot-pedal key, default F13. Press starts local `System.Speech` dictation and prepares the active-window capture; release finalizes the transcript and sends it through the selected Worker, Anthropic, or OpenAI chat provider. An installed Windows recognition language and usable default microphone are required.
- **Element Pointing and motion**: The tutor must end with `[POINT:x,y:label]`, `[POINT:x,y:label:screenN]`, or `[POINT:none]`. The parser strips the directive, the mapper converts capture pixels to physical desktop coordinates, and a topmost transparent overlay displays the target without activation or hit testing. Default-on cue motion is enabled only when both Clicky's motion toggle and the Windows reduced-motion preference allow it; the real mouse pointer is never moved.
- **Settings and secrets**: Provider, Worker URL, model IDs, capture optimization, motion, pedal key, and other non-secret settings are memory-only. Direct Anthropic/OpenAI keys persist in Windows Credential Manager under `Clicky/ProviderApiKey/v1/{provider}` and are never redisplayed; legacy Knobnote targets migrate on first use. Capture-only-during-request, companion exclusion, large-capture optimization, and motion are on; pointer, local retention, and all-displays are off. GDI capture does not explicitly remove visible Clicky overlap.
- **Branding and startup**: Embedded `Clicky.png` and `Clicky.ico` resources provide companion, splash, tray, and executable artwork. Startup shows a reduced-motion-aware, non-activating animated splash for a nominal 1.35 seconds and is currently silent.
- **Verification boundary**: Windows tests use fakes and local HTTP/SSE fixtures. Do not claim live Cloudflare Worker, Anthropic, or OpenAI API E2E verification unless it is explicitly performed and evidenced.

See `windows/README.md` for setup and limitations and `windows/ARCHITECTURE.md` for the full component flow.

### API Proxy (Cloudflare Worker)

The app never calls external APIs directly. All requests go through a Cloudflare Worker (`worker/src/index.ts`) that holds the real API keys as secrets.

| Route | Upstream | Purpose |
|-------|----------|---------|
| `POST /chat` | `api.anthropic.com/v1/messages` | Claude vision + streaming chat |
| `POST /tts` | `api.elevenlabs.io/v1/text-to-speech/{voiceId}` | ElevenLabs TTS audio |
| `POST /transcribe-token` | `streaming.assemblyai.com/v3/token` | Fetches a short-lived (480s) AssemblyAI websocket token |

Worker secrets: `ANTHROPIC_API_KEY`, `ASSEMBLYAI_API_KEY`, `ELEVENLABS_API_KEY`
Worker vars: `ELEVENLABS_VOICE_ID`

### Key Architecture Decisions

**Menu Bar Panel Pattern**: The companion panel uses `NSStatusItem` for the menu bar icon and a custom borderless `NSPanel` for the floating control panel. This gives full control over appearance (dark, rounded corners, custom shadow) and avoids the standard macOS menu/popover chrome. The panel is non-activating so it doesn't steal focus. A global event monitor auto-dismisses it on outside clicks.

**Cursor Overlay**: A full-screen transparent `NSPanel` hosts the blue cursor companion. It's non-activating, joins all Spaces, and never steals focus. The cursor position, response text, waveform, and pointing animations all render in this overlay via SwiftUI through `NSHostingView`.

**Global Push-To-Talk Shortcut**: Background push-to-talk uses a listen-only `CGEvent` tap instead of an AppKit global monitor so modifier-based shortcuts like `ctrl + option` are detected more reliably while the app is running in the background.

**Shared URLSession for AssemblyAI**: A single long-lived `URLSession` is shared across all AssemblyAI streaming sessions (owned by the provider, not the session). Creating and invalidating a URLSession per session corrupts the OS connection pool and causes "Socket is not connected" errors after a few rapid reconnections.

**Transient Cursor Mode**: When "Show Clicky" is off, pressing the hotkey fades in the cursor overlay for the duration of the interaction (recording → response → TTS → optional pointing), then fades it out automatically after 1 second of inactivity.

## Key Files

| File | Lines | Purpose |
|------|-------|---------|
| `leanring_buddyApp.swift` | ~89 | Menu bar app entry point. Uses `@NSApplicationDelegateAdaptor` with `CompanionAppDelegate` which creates `MenuBarPanelManager` and starts `CompanionManager`. No main window — the app lives entirely in the status bar. |
| `CompanionManager.swift` | ~1026 | Central state machine. Owns dictation, shortcut monitoring, screen capture, Claude API, ElevenLabs TTS, and overlay management. Tracks voice state (idle/listening/processing/responding), conversation history, model selection, and cursor visibility. Coordinates the full push-to-talk → screenshot → Claude → TTS → pointing pipeline. |
| `MenuBarPanelManager.swift` | ~243 | NSStatusItem + custom NSPanel lifecycle. Creates the menu bar icon, manages the floating companion panel (show/hide/position), installs click-outside-to-dismiss monitor. |
| `CompanionPanelView.swift` | ~761 | SwiftUI panel content for the menu bar dropdown. Shows companion status, push-to-talk instructions, model picker (Sonnet/Opus), permissions UI, DM feedback button, and quit button. Dark aesthetic using `DS` design system. |
| `OverlayWindow.swift` | ~881 | Full-screen transparent overlay hosting the blue cursor, response text, waveform, and spinner. Handles cursor animation, element pointing with bezier arcs, multi-monitor coordinate mapping, and fade-out transitions. |
| `CompanionResponseOverlay.swift` | ~217 | SwiftUI view for the response text bubble and waveform displayed next to the cursor in the overlay. |
| `CompanionScreenCaptureUtility.swift` | ~132 | Multi-monitor screenshot capture using ScreenCaptureKit. Returns labeled image data for each connected display. |
| `BuddyDictationManager.swift` | ~866 | Push-to-talk voice pipeline. Handles microphone capture via `AVAudioEngine`, provider-aware permission checks, keyboard/button dictation sessions, transcript finalization, shortcut parsing, contextual keyterms, and live audio-level reporting for waveform feedback. |
| `BuddyTranscriptionProvider.swift` | ~100 | Protocol surface and provider factory for voice transcription backends. Resolves provider based on `VoiceTranscriptionProvider` in Info.plist — AssemblyAI, OpenAI, or Apple Speech. |
| `AssemblyAIStreamingTranscriptionProvider.swift` | ~478 | Streaming transcription provider. Fetches temp tokens from the Cloudflare Worker, opens an AssemblyAI v3 websocket, streams PCM16 audio, tracks turn-based transcripts, and delivers finalized text on key-up. Shares a single URLSession across all sessions. |
| `OpenAIAudioTranscriptionProvider.swift` | ~317 | Upload-based transcription provider. Buffers push-to-talk audio locally, uploads as WAV on release, returns finalized transcript. |
| `AppleSpeechTranscriptionProvider.swift` | ~147 | Local fallback transcription provider backed by Apple's Speech framework. |
| `BuddyAudioConversionSupport.swift` | ~108 | Audio conversion helpers. Converts live mic buffers to PCM16 mono audio and builds WAV payloads for upload-based providers. |
| `GlobalPushToTalkShortcutMonitor.swift` | ~132 | System-wide push-to-talk monitor. Owns the listen-only `CGEvent` tap and publishes press/release transitions. |
| `ClaudeAPI.swift` | ~291 | Claude vision API client with streaming (SSE) and non-streaming modes. TLS warmup optimization, image MIME detection, conversation history support. |
| `OpenAIAPI.swift` | ~142 | OpenAI GPT vision API client. |
| `ElevenLabsTTSClient.swift` | ~81 | ElevenLabs TTS client. Sends text to the Worker proxy, plays back audio via `AVAudioPlayer`. Exposes `isPlaying` for transient cursor scheduling. |
| `ElementLocationDetector.swift` | ~335 | Detects UI element locations in screenshots for cursor pointing. |
| `DesignSystem.swift` | ~880 | Design system tokens — colors, corner radii, shared styles. All UI references `DS.Colors`, `DS.CornerRadius`, etc. |
| `ClickyAnalytics.swift` | ~121 | PostHog analytics integration for usage tracking. |
| `WindowPositionManager.swift` | ~262 | Window placement logic, Screen Recording permission flow, and accessibility permission helpers. |
| `AppBundleConfiguration.swift` | ~28 | Runtime configuration reader for keys stored in the app bundle Info.plist. |
| `worker/src/index.ts` | ~142 | Cloudflare Worker proxy. Three routes: `/chat` (Claude), `/tts` (ElevenLabs), `/transcribe-token` (AssemblyAI temp token). |

### Windows Key Files

| File | Purpose |
|------|---------|
| `windows/src/Clicky.Windows/App.xaml.cs` | Windows startup composition, tray/window shutdown, provider clients, dictation, global pedal monitor, capture service, and overlay lifecycle. |
| `windows/src/Clicky.Windows/Shell/CompanionWindow.xaml(.cs)` | Companion UI, capture-before-focus handoff, non-activating window style, and placement. |
| `windows/src/Clicky.Windows/ViewModels/CompanionViewModel.cs` | Typed and pedal voice interaction orchestration, cancellation, response state, and point cue presentation. |
| `windows/src/Clicky.Windows/Input/` | Global F13-F24 pedal monitoring and press/release transition tracking. |
| `windows/src/Clicky.Windows/Voice/` | Local `System.Speech` dictation lifecycle and setup failures. |
| `windows/src/Clicky.Windows/Motion/CompanionMotionPolicy.cs` | Combines the app motion toggle with the Windows reduced-motion preference. |
| `windows/src/Clicky.Windows/Capture/ActiveWindowCaptureService.cs` | Foreground-window DWM bounds, GDI capture, and settings-aware image processing. |
| `windows/src/Clicky.Windows/Capture/CaptureOptimizationPolicy.cs` | Default-on 1920x1080 threshold and half-scale encoding plan. |
| `windows/src/Clicky.Windows/Capture/CaptureImageProcessor.cs` | Resizing/JPEG pipeline that preserves physical capture bounds. |
| `windows/src/Clicky.Windows/Interaction/TutorInteractionService.cs` | General visual-guide requests, 10-turn in-memory history, `POINT` parsing, and coordinate mapping. |
| `windows/src/Clicky.Windows/Tutoring/VisualGuideTutor.cs` | Generic screenshot-grounded visual-guide prompt and terminal `POINT` contract. |
| `windows/src/Clicky.Windows/Networking/` | Provider routing, Worker/Anthropic/OpenAI serialization, authentication, and SSE parsing. |
| `windows/src/Clicky.Windows/Overlay/` | DPI-aware, click-through, non-activating point cue. |
| `windows/src/Clicky.Windows/Shell/TrayIconHost.cs` | Notification-area icon and commands. |
| `windows/src/Clicky.Windows/Configuration/CompanionSettings.cs` | In-memory provider, model, capture optimization, and privacy defaults. |
| `windows/src/Clicky.Windows/Providers/WindowsCredentialApiKeyStore.cs` | Anthropic/OpenAI keys stored under Clicky-owned Windows Credential Manager targets. |
| `windows/src/Clicky.Windows/Shell/StartupSplashWindow.xaml(.cs)` | Reduced-motion-aware, non-activating startup animation. |
| `windows/src/Clicky.Windows/Assets/Clicky.{png,ico}` | Windows companion, splash, tray, and executable artwork. |
| `windows/tests/Clicky.Windows.Tests/` | Unit and local-fixture coverage; not live API E2E coverage. |
| `windows/README.md` | Windows setup, privacy defaults, commands, limitations, and milestones. |
| `windows/ARCHITECTURE.md` | Windows request flow, contracts, component boundaries, and key files. |

## Build & Run

```bash
# Open in Xcode
open leanring-buddy.xcodeproj

# Select the leanring-buddy scheme, set signing team, Cmd+R to build and run

# Known non-blocking warnings: Swift 6 concurrency warnings,
# deprecated onChange warning in OverlayWindow.swift. Do NOT attempt to fix these.
```

**Do NOT run `xcodebuild` from the terminal** — it invalidates TCC (Transparency, Consent, and Control) permissions and the app will need to re-request screen recording, accessibility, etc.

### Windows (.NET 10)

Run from the repository root with the system SDK:

```powershell
dotnet build .\windows\Clicky.Windows.sln
dotnet test .\windows\Clicky.Windows.sln
dotnet run --project .\windows\src\Clicky.Windows\Clicky.Windows.csproj
```

Or use the repository-local SDK:

```powershell
.\.dotnet\dotnet.exe build .\windows\Clicky.Windows.sln
.\.dotnet\dotnet.exe test .\windows\Clicky.Windows.sln
.\.dotnet\dotnet.exe run --project .\windows\src\Clicky.Windows\Clicky.Windows.csproj
```

Real answers require either a compatible Worker URL or a direct Anthropic/OpenAI key configured in Clicky Settings. Non-secret values are held only in memory; direct keys persist in Windows Credential Manager. Building and running unit tests does not verify live Worker or provider access end to end.

## Cloudflare Worker

```bash
cd worker
npm install

# Add secrets
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler secret put ASSEMBLYAI_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY

# Deploy
npx wrangler deploy

# Local dev (create worker/.dev.vars with your keys)
npx wrangler dev
```

## Code Style & Conventions

### Variable and Method Naming

IMPORTANT: Follow these naming rules strictly. Clarity is the top priority.

- Be as clear and specific with variable and method names as possible
- **Optimize for clarity over concision.** A developer with zero context on the codebase should immediately understand what a variable or method does just from reading its name
- Use longer names when it improves clarity. Do NOT use single-character variable names
- Example: use `originalQuestionLastAnsweredDate` instead of `originalAnswered`
- When passing props or arguments to functions, keep the same names as the original variable. Do not shorten or abbreviate parameter names. If you have `currentCardData`, pass it as `currentCardData`, not `card` or `cardData`

### Code Clarity

- **Clear is better than clever.** Do not write functionality in fewer lines if it makes the code harder to understand
- Write more lines of code if additional lines improve readability and comprehension
- Make things so clear that someone with zero context would completely understand the variable names, method names, what things do, and why they exist
- When a variable or method name alone cannot fully explain something, add a comment explaining what is happening and why

### Swift/SwiftUI Conventions

- Use SwiftUI for all UI unless a feature is only supported in AppKit (e.g., `NSPanel` for floating windows)
- All UI state updates must be on `@MainActor`
- Use async/await for all asynchronous operations
- Comments should explain "why" not just "what", especially for non-obvious AppKit bridging
- AppKit `NSPanel`/`NSWindow` bridged into SwiftUI via `NSHostingView`
- All buttons must show a pointer cursor on hover
- For any interactive element, explicitly think through its hover behavior (cursor, visual feedback, and whether hover should communicate clickability)

### Do NOT

- Do not add features, refactor code, or make "improvements" beyond what was asked
- Do not add docstrings, comments, or type annotations to code you did not change
- Do not try to fix the known non-blocking warnings (Swift 6 concurrency, deprecated onChange)
- Do not rename the project directory or scheme (the "leanring" typo is intentional/legacy)
- Do not run `xcodebuild` from the terminal — it invalidates TCC permissions

## Git Workflow

- Branch naming: `feature/description` or `fix/description`
- Commit messages: imperative mood, concise, explain the "why" not the "what"
- Do not force-push to main

## Self-Update Instructions

<!-- AI agents: follow these instructions to keep this file accurate. -->

When you make changes to this project that affect the information in this file, update this file to reflect those changes. Specifically:

1. **New files**: Add new source files to the "Key Files" table with their purpose and approximate line count
2. **Deleted files**: Remove entries for files that no longer exist
3. **Architecture changes**: Update the architecture section if you introduce new patterns, frameworks, or significant structural changes
4. **Build changes**: Update build commands if the build process changes
5. **New conventions**: If the user establishes a new coding convention during a session, add it to the appropriate conventions section
6. **Line count drift**: If a file's line count changes significantly (>50 lines), update the approximate count in the Key Files table

Do NOT update this file for minor edits, bug fixes, or changes that don't affect the documented architecture or conventions.
