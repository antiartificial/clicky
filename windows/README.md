# Clicky for Windows

`windows/` contains the Clicky Windows prototype: a .NET 10 WPF visual guide for typed or foot-pedal voice questions about any active desktop application. It can help with the interface currently visible in an Adobe app, Visual Studio, Rive, a browser, or another desktop tool; these are examples, not built-in integrations. Clicky can use the repository's existing Cloudflare Worker or connect directly to Anthropic or OpenAI with a key stored in Windows Credential Manager. Windows voice input uses local `System.Speech` dictation rather than the macOS transcription pipeline.

For the component-level design, see [ARCHITECTURE.md](ARCHITECTURE.md).

## What works

- A compact, topmost companion window and a Windows notification-area tray shell.
- A typed-question flow that preserves the active application's context. The companion is non-activating while idle; clicking the primary action captures the foreground window first, then activates Clicky and focuses the question box.
- A global, configurable F13-F24 foot-pedal shortcut, default F13. Pressing the pedal starts local Windows dictation and prepares the active-window capture; releasing it finalizes the transcript and processes the question through the provider selected in Settings.
- Active-window JPEG capture using Windows/DWM bounds and GDI `CopyFromScreen`.
- A default-on **Optimize large captures** setting. Images at or below 1920x1080 remain full size; if either dimension is larger, both encoded dimensions are reduced to exactly half scale (with odd dimensions rounded up), encoded at JPEG quality 88, and mapped against the unchanged physical window bounds. Capture work runs off the UI thread and rejects source windows above a 40-million-pixel safety ceiling before bitmap allocation.
- Three AI modes selected in Settings: Cloudflare Worker, direct Anthropic, and direct OpenAI.
- Streaming, screenshot-aware requests through the Worker's Anthropic-compatible `/chat` contract, Anthropic's Messages API, or OpenAI's Responses API.
- Provider-specific model IDs, with `claude-sonnet-4-6` as the Anthropic default and `gpt-5.4-mini` as the OpenAI default.
- Direct-provider key storage in Windows Credential Manager, with masked entry plus save, replace, status, and remove controls.
- A generic `VisualGuideTutor` prompt that treats the active-window title and screenshot as authoritative, teaches one visible action at a time, defines unfamiliar interface terms, avoids inventing controls, asks for clarification when uncertain, and requires one terminal `[POINT:...]` directive.
- Terminal `POINT` parsing, capture-pixel to physical-desktop coordinate mapping, and a labeled, click-through, non-activating topmost cue overlay. Cue motion is on by default and runs only when both Clicky's motion toggle and the Windows reduced-motion preference allow it. The real mouse pointer is never moved.
- In-memory conversation history capped at 10 turns. Changing provider cancels prepared work and clears that history.
- Embedded `Clicky.png` and `Clicky.ico` assets used by the companion, startup splash, tray icon, and executable.
- A reduced-motion-aware, non-activating startup splash with a nominal 1.35-second logo/focus-ring animation. Startup is currently silent.
- Unit tests for provider routing, key management, request serialization, SSE parsing, settings, state transitions, pedal transitions, capture contracts, tutor interactions, `POINT` mapping, cue motion, window placement, view-model behavior, and overlay presentation.

## Interaction flow

1. Leave the application you want help with as the active foreground window and click Clicky's primary action.
2. Clicky enters its listening/preparation state and captures that foreground window before taking focus.
3. After capture succeeds, Clicky activates and focuses the typed-question field.
4. Press Enter or click Send. Clicky sends the active-window title, typed question, prepared JPEG, generic visual-guide prompt, and recent conversation turns through the provider selected in Settings.
5. The streamed response is assembled, its terminal `[POINT:x,y:label]` or `[POINT:none]` directive is removed from the displayed reply, and any point is mapped back into the captured window's desktop bounds.
6. A non-activating overlay marks the target without accepting input or taking focus from the application being guided.

Escape cancels the current interaction. Closing the companion hides it; the tray menu can show it, open Settings, or quit Clicky.

For voice input, configure the **Foot pedal key** in Settings (`F13` by default). Pressing it globally starts listening with local Windows `System.Speech` dictation while Clicky prepares the active-window capture. Releasing it finalizes the transcript and processes it through the selected Worker, Anthropic, or OpenAI chat provider. Windows must have a recognition language installed and a usable default microphone configured.

## Choose an AI provider

Open Settings with the gear button or the tray menu, then select one mode:

| Mode | Setup | Request destination |
|---|---|---|
| Worker | Enter a compatible Worker base URL. No local provider key is used. | `<Worker URL>/chat` |
| Anthropic | Keep or edit the model ID, then store an Anthropic API key. | Fixed `https://api.anthropic.com/v1/messages` |
| OpenAI | Keep or edit the model ID, then store an OpenAI API key. | Fixed `https://api.openai.com/v1/responses` |

Worker is selected when the app starts. All non-secret settings, including provider selection, Worker URL, model IDs, motion, and pedal key, are held in memory and return to their defaults after restart. Saved Anthropic and OpenAI keys remain in Windows Credential Manager until removed.

Changing provider deliberately cancels any captured-but-not-submitted question and clears the in-memory conversation history. A request also snapshots its provider before streaming starts, so it cannot jump providers midway through a response.

## Safe API key setup

For direct Anthropic or OpenAI access:

1. Obtain the key from your own provider account.
2. Open Clicky Settings and select `Anthropic` or `OpenAI`.
3. Confirm the Model ID. The defaults are `claude-sonnet-4-6` and `gpt-5.4-mini`, respectively.
4. Paste the key into the masked `API key` field and click `Save key`.
5. Confirm that the status reads `Stored on this PC`. The entry field is cleared after the save attempt.

To rotate a key, enter the replacement and click `Replace key`. To delete it, click `Remove key`. Clicky does not reveal a saved key and does not test it automatically when saving; the first real request is the provider validation point.

Never paste an API key into a Clicky question, chat message, issue, source file, command line, screenshot, or Worker URL. Use only the masked key field in Settings. Clicky sends the selected direct-provider key in that provider's authentication header, never in the request body.

Keys are stored as provider-specific generic credentials for the current Windows user on the local machine. This protects them at rest better than a settings file, but it is not isolation from a compromised Windows session: another process running as the same user may be able to read the credentials. Keep the Windows account and installed software trusted, and remove keys from Settings when they are no longer needed.

The Credential Manager targets are `Clicky/ProviderApiKey/v1/anthropic` and `Clicky/ProviderApiKey/v1/openai`.
Keys saved by the earlier Windows prototype under `Knobnote/ProviderApiKey/v1/...` are migrated to the Clicky targets on first use and removed from the legacy target only after a successful write.

## Worker setup

The default Worker URL is intentionally a placeholder. Deploy or run the existing Worker described in the root [README.md](../README.md), then select `Worker` in Clicky Settings and enter its base URL, for example:

```text
https://your-worker-name.your-subdomain.workers.dev
```

Do not append `/chat`; the client constructs that route. Remote Worker URLs must use HTTPS and cannot contain embedded credentials. Plain HTTP is accepted only for loopback development addresses such as `http://localhost:8787` or `http://127.0.0.1:8787`.

The configured service must expose the repository's compatible streaming `/chat` endpoint. The Worker owns its upstream provider credentials; Clicky does not attach a locally stored Anthropic or OpenAI key in Worker mode.

## Direct-provider transport

Direct endpoints are constants in the application and are not editable in Settings. Their production HTTP clients disable automatic redirects so an authorization header cannot be forwarded to a different destination.

- Anthropic mode sends the system prompt, conversation turns, typed question, and base64 JPEG to the Messages API. It streams text deltas through Anthropic SSE, requires a terminal `message_stop` event, and rejects `max_tokens` truncation as incomplete.
- OpenAI mode sends the same tutoring context and JPEG as an `input_image` data URL to the Responses API. It sets `stream: true` and `store: false`, consumes `response.output_text.delta`, and requires `response.completed`.

Provider errors shown in the UI are bounded and sanitized. API keys and arbitrary error bodies are not included in user-facing exception messages.

The production Worker client also disables redirects. Combined with the HTTPS-only remote URL rule, this keeps screenshots, questions, and history on the configured Worker origin.

## Privacy and capture scope

The visible settings surface includes provider configuration, the default-on **Motion** toggle, an F13-F24 **Foot pedal key** selector, a noninteractive capture-scope row showing `Active window`, and the default-on **Optimize large captures** toggle.

Optimization is applied only when the captured width exceeds 1920 pixels or its height exceeds 1080 pixels. It reduces both encoded dimensions to half scale before JPEG quality-88 encoding. The `CaptureResult` keeps the original physical desktop bounds, so a point returned in the smaller encoded image still maps proportionally to the correct location in the full-size window. Turning the toggle off keeps the capture at full resolution.

`CompanionSettings` reserves these internal defaults for future capture implementations:

| Reserved field | Default | Current behavior |
|---|---:|---|
| Capture all displays | Off | The prototype captures only the active foreground window. |
| Capture only during an active request | On | A capture is made only while preparing a question. |
| Exclude Clicky windows | On | Capture-before-focus preserves the user's foreground HWND, but GDI does not explicitly remove visible Clicky pixels from the image. |
| Include pointer in captures | Off | The current GDI capture does not add a pointer image. |
| Retain captures locally | Off | Captures remain in memory and are not written to disk by the app. |

The five capture fields are reserved internal model defaults, not visible controls or a selectable policy surface. Images, typed questions or finalized voice transcripts, and recent conversation turns are sent only when a question is processed, through the provider currently selected in Settings. Speech recognition itself is local.

## Build, test, and run

The solution targets `net10.0-windows` and requires the .NET 10 SDK.

For the shortest path, double-click `clicky.bat` in the repository root. It
builds the Release configuration and launches Clicky. The same launcher also
works from PowerShell:

```powershell
.\clicky.bat          # Build and run
.\clicky.bat build    # Build only
.\clicky.bat test     # Run the test suite
```

The launcher automatically uses the repository-local `.dotnet` SDK when it is
available and falls back to the system `dotnet` command.

From the repository root:

```powershell
dotnet build .\windows\Clicky.Windows.sln
dotnet test .\windows\Clicky.Windows.sln
dotnet run --project .\windows\src\Clicky.Windows\Clicky.Windows.csproj
```

This checkout also supports a repository-local SDK at `.dotnet`:

```powershell
.\.dotnet\dotnet.exe build .\windows\Clicky.Windows.sln
.\.dotnet\dotnet.exe test .\windows\Clicky.Windows.sln
.\.dotnet\dotnet.exe run --project .\windows\src\Clicky.Windows\Clicky.Windows.csproj
```

The automated suite uses fake key stores and local HTTP/SSE fixtures. It does not read real Windows credentials or call a live Cloudflare Worker, Anthropic API, or OpenAI API by default. Live-provider behavior therefore requires a separate, deliberate manual verification with the user's own account and billing controls.

## Current limitations

- Responses are text-only. TTS, audio playback, and startup audio are not implemented.
- Voice input requires an installed Windows Speech Recognition language and a usable default microphone.
- There is no packaged installer or signed release artifact.
- Capture is limited to the active foreground window through GDI screen copying. There is no full-display, multi-display, Windows Graphics Capture, or occlusion-independent capture path yet.
- Provider selection, Worker URL, model IDs, motion preference, pedal key, and conversation history are in memory and reset on restart. Direct-provider keys are the exception and persist in Windows Credential Manager.
- Capture scope is fixed to the active window. Except for large-capture optimization, the reserved capture fields are not user-configurable, and visible Clicky overlap is not explicitly removed from GDI captures.
- Automated tests do not make billable live-provider requests or validate account-specific model access.

## Next milestones

1. Add TTS playback and cancellation.
2. Replace or supplement GDI with an explicit Windows Graphics Capture pipeline, then wire the display, pointer, exclusion, and retention settings to real capture policies.
3. Persist non-secret provider preferences and add clear-data controls for any future retained content.
4. Add an explicitly opted-in live integration harness that never logs or commits credentials.
5. Produce a signed, versioned installer with upgrade and uninstall behavior.
