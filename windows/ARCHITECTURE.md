# Clicky Windows Architecture

## Scope

The Windows subtree is a .NET 10 prototype of Clicky as a typed and foot-pedal voice, screenshot-grounded visual guide for any active desktop application. It can help with interfaces such as Adobe apps, Visual Studio, or Rive based on what is visible; these are examples, not application-specific integrations. It can share the existing Cloudflare Worker contract or connect directly to Anthropic and OpenAI. It is otherwise separate from the Swift/macOS application. It currently covers capture and adaptive encoding, typed input, local `System.Speech` dictation, global pedal monitoring, multi-provider streaming chat, response parsing, coordinate mapping, optional cue motion, secure local key storage, settings UI, startup splash, and tray lifecycle. TTS/audio, non-secret settings persistence, and packaging are outside the implemented scope.

## Runtime shape

- **UI:** WPF on `net10.0-windows`, with per-monitor V2 DPI awareness.
- **Tray:** Windows Forms `NotifyIcon` hosted alongside WPF.
- **Composition:** `App.xaml.cs` constructs the services and view model directly at startup.
- **State:** `CompanionSessionCoordinator` owns the `Idle -> Listening -> Processing -> Responding` state machine and interaction IDs.
- **Routing:** `ProviderRoutingChatClient` selects Worker, Anthropic, or OpenAI from `CompanionSettings` before an async response is enumerated.
- **Networking:** one settings-aware Worker client plus dedicated no-redirect direct-provider clients stream text over SSE.
- **Voice:** `GlobalPushToTalkMonitor` supplies global F13-F24 press/release transitions and `SystemSpeechDictationTranscriber` performs local dictation through the default microphone.
- **Motion:** `CompanionMotionPolicy` enables cue motion only when the default-on app setting and Windows client-area animations are both enabled.
- **Secrets:** `WindowsCredentialApiKeyStore` uses the Win32 Credential Manager API for provider-specific generic credentials.
- **Native interop:** User32, DWM, GDI, Credential Manager, and monitor/DPI APIs provide capture, window styles, secure local key storage, coordinate bounds, and overlay placement.
- **Startup:** a non-activating, reduced-motion-aware WPF splash runs a nominal 1.35-second logo/focus-ring sequence before the companion appears.

## Request sequence

```text
User clicks the primary action or presses the configured pedal key
    -> typed: CompanionViewModel.BeginQuestionEntryAsync
    -> voice press: CompanionViewModel.BeginVoiceInteractionAsync
    -> CompanionSessionCoordinator: Listening
    -> TutorInteractionService.PrepareAsync
    -> ActiveWindowCaptureService captures the foreground HWND
    -> typed: CompanionWindow focuses the text box; user submits the question
    -> voice: SystemSpeechDictationTranscriber listens locally until pedal release
       -> CompanionViewModel.CompleteVoiceInteractionAsync finalizes the transcript
    -> CompanionSessionCoordinator: Processing
    -> TutorInteractionService.RespondAsync
    -> ProviderRoutingChatClient snapshots the selected provider
       -> SettingsAwareWorkerClient -> CloudflareWorkerClient
       -> AnthropicDirectApiClient
       -> OpenAiResponsesClient
    -> selected client sends the JPEG, question, prompt, and history
    -> selected SSE reader yields text deltas
    -> PointResponseParser removes the required terminal POINT directive
    -> CoordinateMapper maps image pixels into physical desktop pixels
    -> PointCuePresenter shows a non-activating cue
    -> CompanionSessionCoordinator: Responding
```

Capturing before question-field focus is intentional. If the companion activated first, the foreground-window capture service would capture Clicky instead of the application the user is asking about. A pedal press starts dictation and capture preparation together; release finalizes the transcript before the shared response path sends it through the selected chat provider.

`ProviderRoutingChatClient` resolves the selected client before returning the response enumerable. The provider therefore cannot change midway through one stream. Selecting another provider through the view model first cancels the current or prepared interaction, clears the tutor's in-memory history, updates the selection, and refreshes that provider's key status.

## Capture and coordinates

`ActiveWindowCaptureService` synchronously snapshots the current foreground HWND, title, physical bounds, and optimization setting before Clicky can take focus. It rejects minimized or invalid windows and source dimensions above a checked 40-million-pixel ceiling before allocating a bitmap. GDI copy, resize, and JPEG work then run on a background task with cancellation checks. `CaptureResult` keeps the encoded bytes, encoded image dimensions, original physical desktop bounds, and window title in memory.

`OptimizeLargeCaptures` is on by default. `CaptureOptimizationPolicy` leaves an image at full size when both dimensions are at or below 1920x1080. If its width is greater than 1920 or its height is greater than 1080, both encoded dimensions are reduced to half scale (odd values round up to an integer). `CaptureJpegEncoder` uses high-quality bicubic resizing and JPEG quality 88. The physical bounds are not resized, so `CoordinateMapper` still maps points from encoded screenshot space into the full-size desktop window.

The current request labels this single capture as primary screen/screen 1. `CoordinateMapper` clamps a parsed image-space point to the JPEG dimensions and maps it proportionally into the captured physical bounds. The coordinate types support labeled screens, but the active implementation provides only one coordinate space because only one window is captured.

This is screen copying, not an occlusion-independent window capture API. Covered portions of the active window reflect what is actually visible on the desktop, including any visible Clicky overlap.

## Provider routing and contracts

All three transports implement the existing `IWorkerClient`/`WorkerChatRequest` abstraction. The names are historical; direct providers do not pass through the Worker. `TutorInteractionService` builds one provider-neutral logical request containing the generic `VisualGuideTutor` system prompt, active-window title, current question, active-window JPEG, up to 10 in-memory user/assistant turns, and a 1024-token output limit.

## Voice input

`GlobalPushToTalkMonitor` installs a system-wide low-level keyboard hook for the selected F13-F24 key, default F13, and publishes one press/release transition per physical hold. Changing the in-memory pedal setting updates the monitored key.

`SystemSpeechDictationTranscriber` loads a local `DictationGrammar`, uses the default audio input, and accumulates recognized phrases. It requires an installed Windows recognition language and a usable default microphone. Recognition starts on pedal press; pedal release stops recognition, finalizes the transcript, and routes the text through the same selected Worker, Anthropic, or OpenAI chat path as a typed question. Dictation is local, but the finalized transcript becomes provider request content. There is no TTS or audio playback path.

### Worker

`SettingsAwareWorkerClient` reads `CompanionSettings.WorkerBaseUrl` when each request starts and rejects the repository placeholder before network access. `CloudflareWorkerClient` replaces any base path with `/chat` and sends the repository's Anthropic-compatible body using model `claude-sonnet-4-6`, `stream: true`, and the logical request content.

Worker URLs must be absolute HTTP(S) URLs without embedded credentials. Remote addresses require HTTPS; HTTP is accepted only when `Uri.IsLoopback` is true for local development. Worker mode never reads or attaches a locally stored direct-provider key. Upstream credentials and routing remain the Worker's responsibility.

The production Worker `HttpClientHandler` also disables automatic redirects, so a redirect response cannot forward the JPEG, question, or history to another origin.

The Worker SSE path accepts Anthropic `content_block_delta` text events and terminates on `message_stop` or the Worker's legacy `[DONE]` marker.

### Anthropic direct

`AnthropicDirectApiClient` owns a production `HttpClientHandler` with `AllowAutoRedirect = false` and always posts to:

```text
https://api.anthropic.com/v1/messages
```

It retrieves the Anthropic key from Credential Manager for each request and sends it only in `x-api-key`, alongside `anthropic-version: 2023-06-01`. The body uses `CompanionSettings.AnthropicModelId`, whose default is `claude-sonnet-4-6`, and contains the system prompt, conversation messages, the current base64 JPEG image block and label, `max_tokens`, and `stream: true`.

`AnthropicSseReader` yields `content_block_delta`/`text_delta` content. Direct mode requires a terminal `message_stop`; premature completion, `max_tokens` truncation, and mid-stream Anthropic error events become typed, sanitized failures. Error documents and SSE events are bounded before parsing.

### OpenAI direct

`OpenAiResponsesClient` owns a production `HttpClientHandler` with `AllowAutoRedirect = false` and always posts to:

```text
https://api.openai.com/v1/responses
```

It retrieves the OpenAI key from Credential Manager for each request and sends it only as `Authorization: Bearer`. The Responses body uses `CompanionSettings.OpenAIModelId`, whose default is `gpt-5.4-mini`; maps the system prompt to `instructions`; maps history and the current question to typed input content; includes the JPEG as a base64 data URL in an `input_image`; and sets `stream: true`, `store: false`, and `max_output_tokens`.

`OpenAiResponsesSseReader` yields `response.output_text.delta` content and requires `response.completed`. Refusals, incomplete/failed responses, malformed or oversized events, premature EOF, transport failures, and server-side stream errors become typed, sanitized failures.

Direct endpoints are constants rather than user settings. Disabling redirects prevents the clients from forwarding their authentication headers to a redirect destination. Direct-provider exceptions expose safe categories, HTTP status, and sanitized request identifiers where available; they do not include API keys or arbitrary response bodies.

## Credential boundary

`WindowsCredentialApiKeyStore` stores separate generic credentials under stable application-owned targets:

```text
Clicky/ProviderApiKey/v1/anthropic
Clicky/ProviderApiKey/v1/openai
```

Credentials use Windows `CRED_PERSIST_LOCAL_MACHINE`, scoped to the current user on the local PC. Temporary managed and unmanaged key buffers are zeroed where the implementation can do so. Worker is rejected by the key-store contract because Worker mode has no local provider key.

On first read, the store also recognizes the earlier `Knobnote/ProviderApiKey/v1/...` targets. It writes the value to the Clicky target and deletes the legacy credential only after that write succeeds; explicit removal attempts both target namespaces.

The Settings UI accepts a key through a `PasswordBox`, converts it only for the save call, clears the field afterward, and reports presence without redisplaying any part of the credential. Save does not perform a live provider request. Replace overwrites the provider-specific credential; Remove deletes it.

Credential Manager is at-rest protection, not a sandbox from the signed-in user. Another process running as the same Windows user may be able to read generic credentials. A compromised user session is therefore outside this storage boundary.

Non-secret `CompanionSettings` values are currently in memory only. The app starts in Worker mode with the placeholder Worker URL and default model IDs on every launch, while Credential Manager entries persist until removed.

## Tutor response and point cue

`VisualGuideTutor.SystemPrompt` treats the active-window title, screenshot pixels, and visible text as untrusted data rather than instructions. It tells the model to ignore visible prompt injection, protect secrets, and request explicit confirmation before guiding destructive, credential, payment, permission, security, publishing, or sending actions. For ordinary guidance it helps with any visible desktop application, teaches one concrete action at a time, uses exact visible labels, defines unfamiliar interface terms, avoids invented controls, and asks one concise clarifying question when the app or next action is uncertain. Every response must end with exactly one terminal directive:

```text
[POINT:x,y:label]
[POINT:x,y:label:screenN]
[POINT:none]
```

`PointResponseParser` recognizes a directive only at the end of the response. The spoken/displayed text excludes the directive. A missing terminal directive fails the interaction; `[POINT:none]` returns text without a target.

`PointCuePresenter` creates a topmost transparent WPF window on demand. Its native styles include no-activate, transparent, and tool-window flags; hit testing and mouse activation are explicitly suppressed. The cue is DPI-aware, constrained to the target monitor's work area, optionally labeled, and hidden when a new interaction begins or is canceled. When motion is effectively enabled, the overlay window animates the cue to its target and pulses it; the real mouse pointer is never moved.

## Shell and focus behavior

`CompanionWindow` is borderless, topmost, absent from the taskbar, and non-activating in its compact state. Settings temporarily make it activating. The tray icon can toggle the companion, open Settings, or quit. Closing the companion hides it instead of ending the process.

At startup, `StartupSplashWindow` displays the Clicky logo and an animated focus ring without activating, accepting focus, or appearing in the taskbar. `App` holds it for 1.13 seconds and then runs a 0.22-second outro, producing a nominal 1.35-second sequence before revealing the companion. The splash follows the Windows client-area animation preference and skips animation when reduced motion is requested. Startup is currently silent.

After capture succeeds, `QuestionEntryReady` allows the window to activate and focus the text box. Submitting or canceling clears keyboard focus and restores the non-activating style. Interaction IDs, history generations, and cancellation tokens prevent stale asynchronous results from replacing a newer session or repopulating cleared history.

Provider controls are enabled only while Settings is visible and the session is idle. Opening Settings from a completed `Responding` state resets that interaction; provider changes remain unavailable while capture or processing is active.

## Settings and privacy

The visible Settings surface exposes:

- a segmented Worker/Anthropic/OpenAI provider selector
- Worker URL in Worker mode
- provider-specific Model ID and masked API-key controls in direct mode
- a default-on `Motion` toggle combined with the Windows reduced-motion preference
- an F13-F24 `Foot pedal key` selector, default F13
- a noninteractive `Active window` capture-scope row
- a default-on `Optimize large captures` toggle

`CompanionSettings` also reserves these internal defaults for future capture implementations:

- capture all displays: off
- capture only during active request: on
- exclude Clicky windows: on
- include pointer: off
- retain captures locally: off

The current capture path always takes one foreground-window JPEG during request preparation, does not write it to disk, does not explicitly draw the pointer, and does not explicitly remove visible Clicky pixels. Capture-before-focus preserves the user's foreground HWND; it is not window-content exclusion. Large-capture optimization is the only visible capture-policy toggle; the five other capture fields remain reserved internal defaults and do not imply that multiple capture scopes already exist. All non-secret settings, including motion and pedal key, remain memory-only and reset on restart.

## Key files

| Path | Responsibility |
|---|---|
| `src/Clicky.Windows/App.xaml.cs` | Startup composition, splash sequencing, provider clients, dictation, pedal monitoring, and tray/window shutdown lifecycle. |
| `src/Clicky.Windows/Shell/CompanionWindow.xaml(.cs)` | Companion UI, provider/key settings, focus timing, native non-activating style, and placement. |
| `src/Clicky.Windows/Shell/StartupSplashWindow.xaml(.cs)` | Reduced-motion-aware, non-activating startup animation. |
| `src/Clicky.Windows/ViewModels/CompanionViewModel.cs` | Typed and voice interaction orchestration, provider switching, key operations, cancellation, and safe provider errors. |
| `src/Clicky.Windows/Input/` | Global F13-F24 pedal hook and press/release transition tracking. |
| `src/Clicky.Windows/Voice/` | Local `System.Speech` dictation lifecycle and setup failures. |
| `src/Clicky.Windows/Motion/CompanionMotionPolicy.cs` | App and Windows reduced-motion policy. |
| `src/Clicky.Windows/Session/CompanionSessionCoordinator.cs` | Session states and stale-interaction protection. |
| `src/Clicky.Windows/Capture/ActiveWindowCaptureService.cs` | Foreground-window DWM/GDI capture and settings-aware processing handoff. |
| `src/Clicky.Windows/Capture/CaptureOptimizationPolicy.cs` | 1920x1080 threshold and deterministic half-scale encoding plan. |
| `src/Clicky.Windows/Capture/CaptureImageProcessor.cs` | Encoded-size processing that preserves physical desktop bounds. |
| `src/Clicky.Windows/Capture/CaptureJpegEncoder.cs` | Bicubic resize and JPEG quality-88 encoding. |
| `src/Clicky.Windows/Interaction/TutorInteractionService.cs` | Prepared capture, logical chat request, history, parsing, and mapping. |
| `src/Clicky.Windows/Tutoring/VisualGuideTutor.cs` | Generic screenshot-grounded visual-guide system prompt. |
| `src/Clicky.Windows/Networking/ProviderRoutingChatClient.cs` | Per-request provider selection and routing. |
| `src/Clicky.Windows/Networking/SettingsAwareWorkerClient.cs` | Per-request Worker URL resolution and placeholder guard. |
| `src/Clicky.Windows/Networking/CloudflareWorkerClient.cs` | Secure Worker URL validation, `/chat` serialization, and streaming response. |
| `src/Clicky.Windows/Networking/AnthropicDirectApiClient.cs` | Fixed-endpoint Anthropic Messages request and direct authentication. |
| `src/Clicky.Windows/Networking/OpenAiResponsesClient.cs` | Fixed-endpoint OpenAI Responses request and direct authentication. |
| `src/Clicky.Windows/Networking/AnthropicSseReader.cs` | Worker-compatible and strict direct Anthropic SSE parsing. |
| `src/Clicky.Windows/Networking/OpenAiResponsesSseReader.cs` | Strict OpenAI Responses SSE parsing. |
| `src/Clicky.Windows/Providers/WindowsCredentialApiKeyStore.cs` | Provider-specific Windows Credential Manager storage. |
| `src/Clicky.Windows/Configuration/CompanionSettings.cs` | In-memory provider, URL, model, capture optimization, and privacy defaults. |
| `src/Clicky.Windows/Pointing/PointResponseParser.cs` | Terminal `POINT` extraction. |
| `src/Clicky.Windows/Pointing/CoordinateMapper.cs` | Capture-pixel to physical-desktop mapping. |
| `src/Clicky.Windows/Overlay/PointCuePresenter.cs` | Cue lifecycle, DPI-aware layout, and cancellation. |
| `src/Clicky.Windows/Overlay/PointCueWindow.cs` | Click-through, non-activating overlay rendering. |
| `src/Clicky.Windows/Overlay/PointCueMotionPathCalculator.cs` | Cue reveal and travel paths; does not move the system pointer. |
| `src/Clicky.Windows/Shell/TrayIconHost.cs` | Notification-area icon and commands. |
| `src/Clicky.Windows/Assets/Clicky.png` | Companion and startup-splash image asset. |
| `src/Clicky.Windows/Assets/Clicky.ico` | Tray and executable icon asset. |
| `tests/Clicky.Windows.Tests/` | Unit and local-fixture coverage; no live API E2E by default. |

## Build and verification boundaries

Build, test, and run from the repository root with the system .NET 10 SDK:

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

Automated tests use fake credential stores and local HTTP/SSE handlers. They validate routing, request bodies, authentication placement, stream termination, errors, settings transitions, and UI/view-model behavior without reading a real key or making live, billable Worker, Anthropic, or OpenAI requests. Live account permissions and model availability are therefore outside the default suite.

The Windows subtree does not change the macOS Xcode workflow. It also has no installer project, TTS/audio playback, or startup sound yet. The default automated suite is not live Worker, Anthropic, or OpenAI E2E verification.
