# Changelog

Release notes for this community fork of [Lingarr](https://github.com/lingarr-translate/lingarr).

Fork versioning is independent of upstream and starts at **0.1.0**, based on upstream [Lingarr 1.4.0](https://github.com/lingarr-translate/lingarr/releases/tag/1.4.0).

## 0.2.2 – 2026-11-02

5 commits since 0.2.1. This release translates Plex-only titles from five-star ratings, recovers numbered-list batch replies without a retry, waits for the MKV before Arr auto-translation, and adds an optional inline full-width period cleanup.

### Highlights

- **Plex-only titles.** Rating a title five stars in Plex now translates it even when it is not in Radarr or Sonarr, using the mapped file path and without inserting media rows. Library hits keep Arr paths. Season ratings expand into per-episode jobs on the translation queue. The old Hangfire `Execute(MediaType, entityId, ratingKey)` overload remains so in-flight jobs still deserialize.
- **Numbered-list batch recovery.** Models that ignore `response_format` and answer `1. translated line` are parsed as a batch instead of retried. Leading markdown fences are stripped, and the local AI generate API uses the same parser.
- **Replace inline full-width period.** New setting turns a single mid-cue `。` into a space when writing a subtitle file (`你好。我回来了` → `你好 我回来了`). Off by default. Consecutive periods, quoted dialogue, `？`/`！`, and a trailing period are unchanged; combine with **Strip trailing full-width period** to drop the final `。` as well.

### Improvements

- Arr webhook translation no longer treats a missing MKV as already done: if embedded scan cannot see the file or `ffprobe` fails, the media hash is not persisted, and Sonarr/Radarr translation is scheduled 30s after sync with 2- and 10-minute retries.

### Docker

Multi-arch images (`linux/amd64`, `linux/arm64`):

```text
ghcr.io/davidoffsy/lingarr-support-mkv:0.2.2
ghcr.io/davidoffsy/lingarr-support-mkv:latest
```

Development builds from `main` stay on the `main` tag and report `0.0.0-dev`.

## 0.2.1 – 2026-10-22

11 commits since 0.2.0. This release adds optional bilingual ASS sidecars, skips translation when the target subtitle already exists, and makes Custom/OpenCode batch translation more resilient.

### Highlights

- **Bilingual ASS sidecar.** When **Also export bilingual ASS** is enabled, a completed translation also writes a `.bilingual.ass` sidecar next to the monolingual target. Subtitle discovery ignores these files, and proofread or line edits regenerate them without changing the existing SRT/ASS write path.
- **Already-translated detection.** Arr webhooks, scheduled jobs, bulk translation and the Plex five-star trigger now treat an existing sidecar or a full embedded target track (including PGS) as done. Forced-only tracks still translate. **Scan embedded subtitles** continues to control whether embedded tracks may be used as a source.
- **Adaptive batch response format.** Custom over OpenAI Chat Completions starts with `json_schema`, retries a rejected batch as plain JSON, and switches to `json_object` after three consecutive format rejections; a rejected `json_object` disables `response_format` for the rest of the run. Only explicit format rejections change the policy, so context-length and other request errors keep their normal handling.
- **Stable OpenCode session.** `x-opencode-session` is now sent with a stable value for the whole translation run on all three Custom protocols, including Anthropic Messages, so OpenCode Go can pin routing and prompt caching to one backend.

### Improvements

- Known non-MKV videos no longer match a neighboring MKV with the same stem when extracting embedded subtitles; bulk translation uses the same guard.
- Target-language detection matches region codes (`zh-CN` against a `zh` track) and skips `ffprobe` when sidecar state and the MKV fingerprint prove nothing changed.
- New setting **Strip trailing full-width period** removes a single `。` at the end of each cue when writing a subtitle file. Off by default; punctuation inside a line and consecutive periods are unchanged.
- Bilingual ASS output uses a light-yellow source color and collapses extra lines; the existing monolingual SRT/ASS path is unchanged.
- Recoverable batch format probes log the first rejection of a streak as a warning and repeats at debug, so capability probing no longer looks like a failed translation. Genuine failures remain errors.

### Docker

Multi-arch images (`linux/amd64`, `linux/arm64`):

```text
ghcr.io/davidoffsy/lingarr-support-mkv:0.2.1
ghcr.io/davidoffsy/lingarr-support-mkv:latest
```

Development builds from `main` stay on the `main` tag and report `0.0.0-dev`.

## 0.2.0 – 2026-10-17

24 commits since 0.1.0. This release adds a **Custom AI provider** with three protocols, **per-model translation chains** with source attribution, the **Plex five-star trigger** with Plex subtitle streams as a source, **Arr webhook tokens**, and a new **subtitle source ranking**.

Two changes need attention before upgrading: **Arr webhook URLs** and the **`service_type` format**. See [Upgrade notes](#upgrade-notes).

### Highlights

- **Custom AI provider.** `Settings → Services → Custom` talks to any OpenAI- or Anthropic-compatible endpoint. Pick the protocol — **OpenAI Chat Completions**, **OpenAI Responses** or **Anthropic Messages** — then set the address (base URL or full path), an optional API key and the model, with one request template per protocol. **Test connection** loads the model list when the gateway exposes `/models`; otherwise type the model id. Available as `CUSTOM_ENDPOINT`, `CUSTOM_PROTOCOL`, `CUSTOM_API_KEY`, `CUSTOM_MODEL`, `CUSTOM_ANTHROPIC_VERSION` and the `CUSTOM_*_REQUEST_TEMPLATE` keys.
- **Per-model translation chains.** Each entry of the ordered chain is now a provider plus an optional model, so one provider can appear several times with different models and fallback can switch model instead of provider. Chains are validated before saving: unknown providers, duplicate `(provider, model)` pairs and models on providers that cannot override them are rejected.
- **Source attribution.** Translated lines record the provider and model that produced them. Subtitle headers read `multiple translators (custom:model-a, deepl)` when more than one was involved, the translation detail page shows the model per line, and the Dashboard gained **Lines by Source** and **Files by Model** tiles.
- **Plex five-star trigger.** Rating a synced movie or episode five stars in Plex enqueues a translation: Plex Pass webhooks, **Sign in with Plex**, a path mapping and a Lingarr-generated webhook secret. Titles that are not in the Arr library are skipped. See the new [Plex documentation](https://davidoffsy.github.io/lingarr-support-mkv/getting-started/plex).
- **Plex subtitle streams as a source.** When no local subtitle matches, Lingarr imports text subtitle streams (SRT/ASS/SSA) from Plex metadata and writes them as sidecars next to the video.
- **Arr webhook tokens.** Radarr and Sonarr webhooks authenticate with a dedicated secret that is separate from the Lingarr API key: `POST /api/webhook/radarr?token=…`.
- **Auto-translation per Arr app.** Radarr and Sonarr each have their own switch on the webhook settings card. Media sync runs regardless of the switches.

### Improvements

- **Subtitle sources are ranked by dialogue quality**: sidecar before embedded, non-forced before forced, non-SDH/HI/CC before captioned tracks, then track index. The MKV **Default** flag no longer wins, and it is ignored when embedded tracks are merged.
- **Source languages follow the configured order** for automatic jobs and Plex alike. An untagged (`und`/`unknown`) subtitle is handed to AI services with automatic language detection instead of being skipped; providers with a fixed language list still skip it.
- **Chain fallback**: configuration failures (missing address or key, unknown protocol, unusable template, HTTP 401) skip the remaining candidates of that provider; `408/429/502/503/504` and request timeouts are retried (`max_retries`, default 5, with `retry_delay` doubling) before moving down the chain; a failing batch candidate falls back to the line-by-line path instead of failing the file.
- **Per-request timeouts** for Custom and LocalAI: `request_timeout` (default 5 minutes) is enforced per request instead of through a shared `HttpClient` timeout, and a timeout counts as retryable.
- **LocalAI no longer injects** `thinking` or `max_tokens` unless they are configured. The automatic downgrade on HTTP 400/422 is gone, so a gateway that rejects a field fails that candidate instead of being retried without it. Anthropic's default template raises `max_tokens` from 1024 to 8192.
- **Proofreading** keeps unusable providers for the whole file instead of retrying them line by line, and reports the service and model behind the applied suggestion.
- **Statistics** count translated lines per position, so a multi-line cue counts once and **Lines Translated** matches the sum of **Lines by Source**. Missing batch lines are no longer filled with the original text.
- **Settings**: chain rows are laid out as label / provider / actions above a searchable model dropdown that loads models on open and accepts a typed id; a chain that fails validation is not saved.
- Long model ids are stored without truncation; the new attribution data lands in `translation_request_lines.model` and `statistics.lines_by_source_json` through migrations.
- Plex webhook status is queried on demand instead of every five seconds. The fork documentation site builds with working asset paths under GitHub Pages.

### Upgrade notes

1. **Arr webhook URLs must be updated when Lingarr authentication is enabled.** The Radarr and Sonarr endpoints no longer accept the Lingarr API key. They authenticate against `arr_webhook_secret`, which is generated at **Settings → Integrations → Webhooks** and appended as `?token=…`. Instances with authentication disabled keep working until a secret is generated; instances with authentication enabled return 401 until both URLs are replaced. Regenerating the secret invalidates both URLs at once.
2. **`service_type` is now an ordered array** of `{ "provider", "model?" }` entries. A single value such as `openai` is promoted to `[{"provider":"openai"}]` at startup, but a database written by 0.2.0 cannot be read by older Lingarr releases, which only understand the string form.
3. **AI request defaults are reset once.** `ai_thinking` moves from `off` to `auto` and `ai_max_tokens` from `8192` to `0` when they still hold the previous defaults. If you deliberately pinned `off` for a gateway that rejects `thinking`, set it again; `0` means "leave it to the provider or template".
4. **LocalAI compatibility fields are only sent when configured.** A gateway that previously recovered through the automatic retry may now fail that candidate until `ai_thinking` / `ai_max_tokens` are set to the values it accepts.
5. **Line statistics use the new per-position counting.** Historic totals are not recalculated, so the lifetime number mixes both conventions until statistics are reset.
6. **Source track selection changed.** Subtitles translated by 0.1.0 may be re-queued and translated from a different track, because Default/SDH/HI tracks no longer win and untagged subtitles now reach AI services.
7. **Embedded subtitle scanning defaults to enabled on new installations only.** Existing settings are preserved.

### Docker

Multi-arch images (`linux/amd64`, `linux/arm64`):

```text
ghcr.io/davidoffsy/lingarr-support-mkv:0.2.0
ghcr.io/davidoffsy/lingarr-support-mkv:latest
```

Development builds from `main` stay on the `main` tag and report `0.0.0-dev`.

## 0.1.0 – 2026-10-13

First release of this community fork, based on upstream [Lingarr 1.4.0](https://github.com/lingarr-translate/lingarr/releases/tag/1.4.0).

- Extract and translate **text subtitle tracks embedded in MKV** files (FFmpeg / ffprobe)
- On-demand **MKV extract UI** on movies and episodes, with extract progress and safer job timeouts
- **Local AI** protocol selection, connection testing and OpenCode Go session headers
- Retry missing lines in AI batch translations, with configurable max tokens and thinking settings
- Images published to GHCR as `ghcr.io/davidoffsy/lingarr-support-mkv:0.1.0` and `:latest`
- In-app update checks use this repository's GitHub Releases instead of upstream `api.lingarr.com`
