# Clicky for Windows

`windows/` contains the Clicky Windows prototype: a .NET 10 WPF visual guide for typed or foot-pedal voice questions about any active desktop application. It can help with the interface currently visible in an Adobe app, Visual Studio, Rive, a browser, or another desktop tool; these are examples, not built-in integrations. The Windows experience is local-first and BYOK: it connects directly to OpenAI, Anthropic, or Gemini with keys stored in Windows Credential Manager. Windows voice input uses local `System.Speech` dictation rather than the macOS transcription pipeline.

For the component-level design, see [ARCHITECTURE.md](ARCHITECTURE.md).

## What works

- A compact, topmost companion window and a Windows notification-area tray shell.
- A typed-question flow that preserves the active application's context. The companion is non-activating while idle; clicking the primary action captures the foreground window first, then activates Clicky and focuses the question box.
- A global, configurable F13-F24 foot-pedal shortcut, default F13. Pressing the pedal starts local Windows dictation and prepares the active-window capture; releasing it finalizes the transcript and processes the question through the provider selected in Settings.
- Active-window JPEG capture using Windows/DWM bounds and GDI `CopyFromScreen`.
- A default-on **Optimize large captures** setting. Images at or below 1920x1080 remain full size; if either dimension is larger, both encoded dimensions are reduced to exactly half scale (with odd dimensions rounded up), encoded at JPEG quality 88, and mapped against the unchanged physical window bounds. Capture work runs off the UI thread and rejects source windows above a 40-million-pixel safety ceiling before bitmap allocation.
- Three direct AI modes selected in Settings: OpenAI, Anthropic, and Gemini. The legacy Cloudflare Worker transport remains available in the networking layer for compatibility, but it is no longer the default Windows experience.
- Streaming, screenshot-aware requests through Anthropic's Messages API, OpenAI's Responses API, or Gemini's `streamGenerateContent` SSE API. Visible answer deltas gently fade in while the terminal pointer directive remains hidden.
- Provider-specific model IDs, with `gpt-5.4-mini` as the OpenAI default, `claude-sonnet-4-6` as the Anthropic default, and `gemini-3.5-flash` as the Gemini default.
- Direct-provider key storage in Windows Credential Manager, with masked entry plus save, replace, status, and remove controls.
- A generic `VisualGuideTutor` prompt that treats the active-window title and screenshot as authoritative, teaches one visible action at a time, defines unfamiliar interface terms, avoids inventing controls, asks for clarification when uncertain, and requires one terminal `[POINT:...]` directive.
- Terminal `POINT` parsing, capture-pixel to physical-desktop coordinate mapping, and a labeled, click-through, non-activating topmost cue overlay. Cue motion is on by default and runs only when both Clicky's motion toggle and the Windows reduced-motion preference allow it. The real mouse pointer is never moved.
- Active provider context remains capped at 10 turns. Successful turns are also written to `%LocalAppData%\Clicky\clicky.db`; screenshots and secrets are never stored there.
- The conversation pane lists past conversations newest-first with a generated title and rolling answer summary, and opens their complete typed-and-voice transcripts. Starting a new conversation resets only active context and preserves prior sessions.
- Guided OpenAI onboarding stores the key, retrieves account-visible models, recommends a visual-tutoring model, verifies image Responses access, and persists the successful model and validation time.
- Optional OpenAI text-to-speech reads completed answers using the stored OpenAI key. ElevenLabs is available as an optional direct BYOK custom-voice provider with its own Credential Manager entry and voice ID. Playback is serialized, cancellable, and never required for the text answer to succeed.
- Non-secret settings persist atomically in `%LocalAppData%\Clicky\settings.json`.
- Embedded `Clicky.png` and `Clicky.ico` assets used by the companion, startup splash, tray icon, and executable.
- A reduced-motion-aware, non-activating startup splash with a nominal 1.13-second logo/focus-ring animation. Startup remains silent.
- Unit tests for provider routing, key management, request serialization, SSE parsing, settings, state transitions, pedal transitions, capture contracts, tutor interactions, `POINT` mapping, cue motion, window placement, view-model behavior, and overlay presentation.

## Interaction flow

1. Leave the application you want help with as the active foreground window and click Clicky's primary action.
2. Clicky enters its listening/preparation state and captures that foreground window before taking focus.
3. After capture succeeds, Clicky activates and focuses the typed-question field.
4. Press Enter or click Send. Clicky sends the active-window title, typed question, prepared JPEG, generic visual-guide prompt, and recent conversation turns through the provider selected in Settings.
5. Visible answer deltas appear immediately with a subtle reduced-motion-aware fade while Clicky privately holds back the terminal `[POINT:x,y:label]` or `[POINT:none]` directive.
6. The completed turn is written to local SQLite, optional speech playback begins, and a non-activating overlay marks any returned target without accepting input.

Escape cancels the current interaction. Closing the companion hides it; the tray menu can show it, open Settings, or quit Clicky.

For voice input, configure the **Foot pedal key** in Settings (`F13` by default). Pressing it globally starts listening with local Windows `System.Speech` dictation while Clicky prepares the active-window capture. Releasing it finalizes the transcript and processes it through the selected OpenAI, Anthropic, or Gemini provider. Windows must have a recognition language installed and a usable default microphone configured.

## Choose an AI provider

Open Settings with the gear button or the tray menu, then select one mode:

| Mode | Setup | Request destination |
|---|---|---|
| Anthropic | Keep or edit the model ID, then store an Anthropic API key. | Fixed `https://api.anthropic.com/v1/messages` |
| OpenAI | Keep or edit the model ID, then store an OpenAI API key. | Fixed `https://api.openai.com/v1/responses` |
| Gemini | Keep or edit the model ID, then store a Gemini API key. | Fixed `https://generativelanguage.googleapis.com/v1beta/models/...:streamGenerateContent` |

OpenAI is selected on a fresh install. Non-secret settings persist in `%LocalAppData%\Clicky\settings.json`; saved provider keys remain in Windows Credential Manager until removed.

Changing provider deliberately cancels any captured-but-not-submitted question and clears the in-memory conversation history. A request also snapshots its provider before streaming starts, so it cannot jump providers midway through a response.

## Safe API key setup

For direct OpenAI, Anthropic, or Gemini access:

1. Obtain the key from your own provider account.
2. Open Clicky Settings and select `OpenAI`, `Anthropic`, or `Gemini`.
3. Confirm the Model ID. The defaults are `gpt-5.4-mini`, `claude-sonnet-4-6`, and `gemini-3.5-flash`.
4. Paste the key into the masked `API key` field and click `Connect & test`.
5. Clicky stores the key, retrieves the models visible to that OpenAI project, recommends a compatible visual-tutoring model, and runs one small image Responses request.
6. Confirm that the Key, Models, and Vision indicators are complete and the status reads `OpenAI ready`.

To rotate an OpenAI key, enter the replacement and click `Connect & test`. `Test again` rechecks the stored key and currently selected model without replacing the credential. To delete it, click `Remove key`. Clicky never reveals a saved key.

OpenAI onboarding calls `GET /v1/models`, filters the returned IDs to likely Responses-and-image-capable tutoring families, and currently prefers `gpt-5.4-mini` when the account exposes it. Because the model-list response does not advertise endpoint or image capabilities, Clicky treats the small image Responses request as the authoritative compatibility check. A restricted OpenAI key needs read access for Models and write access for Responses; the project must also have API billing or credits and allow the selected model. Validation metadata is non-secret and persists in `settings.json`; changing the model or removing the key invalidates the Ready state. On startup, an unvalidated OpenAI setup opens directly to Settings.

Never paste an API key into a Clicky question, chat message, issue, source file, command line, screenshot, or Worker URL. Use only the masked key field in Settings. Clicky sends the selected direct-provider key in that provider's authentication header, never in the request body.

Keys are stored as provider-specific generic credentials for the current Windows user on the local machine. This protects them at rest better than a settings file, but it is not isolation from a compromised Windows session: another process running as the same user may be able to read the credentials. Keep the Windows account and installed software trusted, and remove keys from Settings when they are no longer needed.

The Credential Manager targets are `Clicky/ProviderApiKey/v1/anthropic`, `.../openai`, `.../gemini`, and `.../elevenlabs`.
Keys saved by the earlier Windows prototype under `Knobnote/ProviderApiKey/v1/...` are migrated to the Clicky targets on first use and removed from the legacy target only after a successful write.

## Legacy Worker compatibility

The Worker transport remains in the codebase for compatibility with the original macOS architecture and local fixtures, but it is not shown in the Windows provider selector. New Windows use should prefer direct BYOK providers. A developer exercising the legacy path must configure `CompanionSettings.WorkerBaseUrl`, for example:

```text
https://your-worker-name.your-subdomain.workers.dev
```

Do not append `/chat`; the client constructs that route. Remote Worker URLs must use HTTPS and cannot contain embedded credentials. Plain HTTP is accepted only for loopback development addresses such as `http://localhost:8787` or `http://127.0.0.1:8787`.

The configured service must expose the repository's compatible streaming `/chat` endpoint. The Worker owns its upstream provider credentials; Clicky does not attach a locally stored Anthropic or OpenAI key in Worker mode.

## Direct-provider transport

Direct endpoints are constants in the application and are not editable in Settings. Their production HTTP clients disable automatic redirects so an authorization header cannot be forwarded to a different destination.

- Anthropic mode sends the system prompt, conversation turns, typed question, and base64 JPEG to the Messages API. It streams text deltas through Anthropic SSE, requires a terminal `message_stop` event, and rejects `max_tokens` truncation as incomplete.
- OpenAI mode sends the same tutoring context and JPEG as an `input_image` data URL to the Responses API. It sets `stream: true` and `store: false`, consumes `response.output_text.delta`, and requires `response.completed`.
- Gemini mode sends alternating user/model turns and the JPEG as `inline_data`, consumes SSE candidate text, and requires a terminal `STOP` finish reason. Safety and incomplete responses become typed, bounded failures.

Provider errors shown in the UI are bounded and sanitized. API keys and arbitrary error bodies are not included in user-facing exception messages.

The legacy production Worker client also disables redirects. It remains covered for compatibility but is not exposed by the current Windows provider selector.

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

The five capture fields are reserved internal model defaults, not visible controls or a selectable policy surface. Images, typed questions or finalized voice transcripts, and recent conversation turns are sent only when a question is processed, through the provider currently selected in Settings. Speech recognition itself is local. SQLite stores transcript text and metadata only; API keys remain in Credential Manager and screenshots remain memory-only.

## Build, test, and run

The solution targets `net10.0-windows` and requires the .NET 10 SDK.

For the shortest path, double-click `clicky.bat` in the repository root. It
builds the Release configuration and launches Clicky. The same launcher also
works from PowerShell:

```powershell
.\clicky.bat          # Build and run
.\clicky.bat build    # Build only
.\clicky.bat test     # Run the test suite
.\clicky.bat doctor   # Check the stored OpenAI key and selected model
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

The automated suite uses fake key stores and local HTTP/SSE fixtures. It does not read real Windows credentials or call a live Cloudflare Worker, Anthropic, OpenAI, Gemini, or ElevenLabs API by default. `clicky.bat doctor` is the deliberate exception: it reads the stored OpenAI credential and makes one small, billable text-only request asking for `OK`. It sends no screenshot or conversation history and reports only a sanitized failure category, provider code, and request ID. The in-app onboarding check is stronger for Clicky's purpose because it also validates a tiny image input.

## Current limitations

- OpenAI and ElevenLabs speech output is implemented, but live account/model/voice access is not exercised by the automated suite. Startup audio is not implemented.
- Voice input requires an installed Windows Speech Recognition language and a usable default microphone.
- There is no packaged installer or signed release artifact.
- Capture is limited to the active foreground window through GDI screen copying. There is no full-display, multi-display, Windows Graphics Capture, or occlusion-independent capture path yet.
- There is no conversation search, AI-generated multi-turn synopsis, export, retention window, or encrypted SQLite option yet. The current rolling summary is a bounded preview of the latest successful answer.
- Capture scope is fixed to the active window. Except for large-capture optimization, the reserved capture fields are not user-configurable, and visible Clicky overlap is not explicitly removed from GDI captures.
- Automated tests do not make billable live-provider requests or validate account-specific model access.

## Next milestones

1. Add conversation search, deletion/export controls, retention settings, and provider-generated rolling summaries.
2. Add OpenAI cloud transcription as an optional pedal input adapter while retaining local Windows speech.
3. Add an optional OpenAI Realtime conversation mode, followed by Gemini Live, without replacing deterministic push-to-talk.
4. Replace or supplement GDI with an explicit Windows Graphics Capture pipeline.
5. Add an explicitly opted-in live integration harness, then produce a signed installer.
