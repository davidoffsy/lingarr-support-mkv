## Settings
These settings can be configured as environment variables to persist configurations across reinstallations.

### **Docker secrets**

Any of the environment variables documented below can also be supplied by appending `_FILE` to the variable name and pointing it at a file containing the value.  
When both `<VAR>` and `<VAR>_FILE` are set, `<VAR>_FILE` will be selected primarily.

Example:

```yaml
services:
  lingarr:
    image: lingarr/lingarr:latest
    environment:
      OPENAI_API_KEY_FILE: /run/secrets/openai_api_key
    secrets:
      - openai_api_key

secrets:
  openai_api_key:
    file: ./secrets/openai_api_key.txt
```

---

### **General Configuration**

| **Environment Variable**   | **Description**                                                                                                                               |
|----------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------|
| `BASE_PATH`                | Optional URL prefix to host Lingarr under a sub-path (e.g., behind a reverse proxy). Example: `/lingarr`. Leave unset to serve from the root. |
| `MAX_CONCURRENT_JOBS`      | Maximum number of Hangfire worker threads processing jobs in parallel. Defaults to `1` when unset.                                            |
| `JOB_TIMEOUT_MINUTES`      | Minutes of silence before Hangfire retries a job. `sqlite` only, defaults to `30`. Effective SQLite timeout is `max(JOB_TIMEOUT_MINUTES, EXTRACT_TIMEOUT_MINUTES)` (extract default `60`). |
| `SQLITE_DB_PATH`           | Path to the SQLite database file used when `DB_CONNECTION` is `sqlite`. Defaults to `local.db` within `/app/config/`.                         |
| `DB_HANGFIRE_SQLITE_PATH`  | Path to the SQLite database file used by the Hangfire job store. Defaults to `/app/config/Hangfire.db`.                                       |
| `FFMPEG_PATH`              | Optional path to the `ffmpeg` binary. Defaults to `ffmpeg` on `PATH`. The Docker image already includes FFmpeg.                               |
| `FFPROBE_PATH`             | Optional path to the `ffprobe` binary. Defaults to `ffprobe` on `PATH`. The Docker image already includes ffprobe.                            |

---

### **Radarr Configuration**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `RADARR_URL`             | The base URL for the Radarr API. Example: `http://localhost:7878`.              |
| `RADARR_API_KEY`         | The API key for authenticating with Radarr. Obtain this from your Radarr instance. |

---

### **Sonarr Configuration**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `SONARR_URL`             | The base URL for the Sonarr API. Example: `http://localhost:8989`.              |
| `SONARR_API_KEY`         | The API key for authenticating with Sonarr. Obtain this from your Sonarr instance. |

Webhook URLs are shown at **Settings → Integrations**. Generate a secret there, then copy the full URL, including the Lingarr-generated `token=` query. Do **not** put Lingarr's `api_key` or the Radarr/Sonarr API keys into the webhook URL. Existing connections without a token keep working until you generate a secret.

---

### **Plex Configuration**

Primary setup is the web UI: **Settings → Integrations → Sign in with Plex**. Plex Web has no copyable API token; do **not** scrape XML or `X-Plex-Token` from Plex.

The environment variables below are a **headless / reinstall fallback only**. Prefer Sign in with Plex.

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `PLEX_URL`               | The Plex Media Server base URL, with no trailing slash. Example: `http://plex:32400`. |
| `PLEX_TOKEN`             | plex.tv access token from Sign in with Plex (sensitive). Prefer UI OAuth; use this only for automation installs. |

Do **not** put `PLEX_TOKEN` or Lingarr's `api_key` into the Plex Webhooks URL. The webhook secret is generated in the UI and appears only as the query `token=` on `POST /api/webhook/plex`.

Keep **Remove language tag** off (`remove_language_tag=false`) so Plex can pick `{stem}.{lang}.srt`. If Plex `Part.file` paths differ from Radarr/Sonarr, add Movie mappings for films and Show mappings for episodes at `/settings/mapping`. Five-star ratings work for movies, episodes, and seasons. Titles that are not in Radarr/Sonarr still translate when Lingarr can read the mapped file; they are not added to the Movies or Shows lists.

---

### **Authentication Configuration**

| **Environment Variable** | **Description**                                                                  |
|--------------------------|----------------------------------------------------------------------------------|
| `AUTH_ENABLED`           | Enables or disables authentication.                                              |
| `ENCRYPTION_KEYS`        | The path for storing encryption keys, stored by default in the config directory. |

---

### **Telemetry Configuration**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `TELEMETRY_ENABLED`      | Opt-in for anonymized telemetry to help other Lingarr users, defaults to false. |

When enabled, Lingarr submits a report at most once every 7 days, and never from a development build. Each report contains the running Lingarr version, the platform, the report date, and the same lifetime totals shown on the Statistics page: files, lines and characters translated, plus the breakdowns by translation service, language pair, media type and model. No file names, subtitle content or other personal information is collected. Resetting statistics resets these totals as well.

Each report also carries an installation identifier. This is a random value generated once on first submission and stored locally as a setting; it is not derived from the machine, the network, the IP address or any hardware. The service that receives the report stores only a hash of it, never the original value, and uses it solely to tell one installation's reports apart from another's over time.

---

### **Translation Service Configuration**

#### **General Translation Settings**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `SERVICE_TYPE`           | An ordered JSON array of translation services. Each entry is a provider name or an object `{ "provider", "model?" }`. Language support is matched first; within the same match, entries are tried in order. A single value (e.g. `openai`) is still accepted and is promoted to `[{"provider":"openai"}]` on startup. The object form is not readable by older Lingarr versions. |
| `SOURCE_LANGUAGES`       | A minified JSON array of source languages for translation. |
| `TARGET_LANGUAGES`       | A minified JSON array of target languages for translation. |
| `USE_BATCH_TRANSLATION`  | Set to `true` to translate subtitles in batches with AI services, defaults to false. |
| `SCAN_EMBEDDED_SUBTITLES` | Set to `true` to probe MKV text tracks (SRT / ASS / SSA) during automatic and bulk translation. Movie and show lists are never probed. Defaults to true for new installations; existing settings are preserved. |
| `EXTRACT_EMBEDDED_SOURCE` | Set to `true` to write the selected source track as a sidecar next to the video so Plex / Emby can switch languages. Defaults to true. Translation of embedded tracks always extracts this file first. |
| `EXTRACT_TIMEOUT_MINUTES` | Minutes ffmpeg may spend copying an embedded text track on demand. Defaults to `60`. Automatic / bulk extract uses a 2-minute timeout. |

The `SOURCE_LANGUAGES` and `TARGET_LANGUAGES` variables should be provided as a minified JSON array. Each object in the array should contain the `name` and `code` of the language.

#### Example:
```json
[{"name":"English","code":"en"},{"name":"Dutch","code":"nl"}]
```

### Service type

#### **LibreTranslate**

| **Environment Variable**     | **Description**                                                                 |
|------------------------------|---------------------------------------------------------------------------------|
| `LIBRE_TRANSLATE_URL`        | The base URL for the LibreTranslate API. Example: `https://libretranslate.com`. |
| `LIBRE_TRANSLATE_API_KEY`    | The API key to use for the LibreTranslate API. This is optional, and only needed if using an instance that requires one. |

#### **OpenAI**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `OPENAI_MODEL`           | The model to use for OpenAI translations. Example: `gpt-4`, `gpt-3.5-turbo`.    |
| `OPENAI_API_KEY`         | The API key for authenticating with OpenAI.                                     |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **Anthropic**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `ANTHROPIC_MODEL`        | The model to use for Anthropic translations. Example: `claude-opus-5`.          |
| `ANTHROPIC_API_KEY`      | The API key for authenticating with Anthropic.                                  |
| `ANTHROPIC_VERSION`      | The version of the Anthropic API to use.                                        |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **Gemini**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `GEMINI_MODEL`           | The model to use for Gemini translations. Example: `gemini-2.0-flash`.          |
| `GEMINI_API_KEY`         | The API key for authenticating with Gemini.                                     |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **DeepSeek**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `DEEPSEEK_MODEL`         | The model to use for DeepSeek translations. Example: `deepseek-chat`.           |
| `DEEPSEEK_API_KEY`       | The API key for authenticating with DeepSeek.                                   |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **Mistral**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `MISTRAL_MODEL`          | The model to use for Mistral translations. Example: `mistral-large-latest`.     |
| `MISTRAL_API_KEY`        | The API key for authenticating with Mistral.                                    |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **xAI**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `XAI_MODEL`              | The model to use for xAI translations. Example: `grok-4.5`.                     |
| `XAI_API_KEY`            | The API key for authenticating with xAI.                                        |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **LocalAI**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `LOCAL_AI_MODEL`         | The model to use for LocalAI translations.                                      |
| `LOCAL_AI_API_KEY`       | The API key for authenticating with LocalAI.                                    |
| `LOCAL_AI_ENDPOINT`      | Base URL or full completion path. Example: `http://ollama:11434` or `https://opencode.ai/zen/go/v1`. |
| `LOCAL_AI_PROTOCOL`      | `auto`, `openai_chat`, or `ollama_generate`. Default `auto`.                    |
| `AI_PROMPT`              | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`         | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

#### **Custom**

Your own gateway over one explicitly selected, non-streaming protocol: `openai_chat` (Chat Completions, default), `openai_responses` (Responses), or `anthropic_messages` (Anthropic Messages). Independent from LocalAI and official Anthropic: address, key, model, protocol and per-protocol request template live under their own `custom_*` settings, so these providers can be used in the same translation chain without sharing any of them. Existing `localai` and official Anthropic configurations keep working unchanged.

| **Environment Variable**        | **Description**                                                                 |
|---------------------------------|---------------------------------------------------------------------------------|
| `CUSTOM_PROTOCOL`               | `openai_chat`, `openai_responses`, or `anthropic_messages`. Default `openai_chat`. The endpoint path, request body and parsed response follow this setting; a full path of another protocol is recognised and replaced, never guessed. |
| `CUSTOM_ENDPOINT`               | Absolute `http` or `https` URL: a base URL or full endpoint path of the selected protocol. A bare base URL gets `/v1/chat/completions`, `/v1/responses`, or `/v1/messages` appended; a URL that already ends in `/v1` gets the matching suffix; a full path is used as it is. Query strings are kept; Azure `/deployments/{id}/...` paths are not rewritten. Example: `http://localhost:8000/v1`. |
| `CUSTOM_MODEL`                  | The model to use. Load the model list with **Test connection**, or type a model id when the gateway has no `/models` endpoint. |
| `CUSTOM_API_KEY`                | Optional. Bearer token for the OpenAI protocols, or the `x-api-key` value for Anthropic Messages. Stored encrypted. |
| `CUSTOM_ANTHROPIC_VERSION`      | `anthropic-version` header for `anthropic_messages`. Default `2023-06-01`. Ignored by the OpenAI protocols. |
| `CUSTOM_CHAT_REQUEST_TEMPLATE`  | The request template for `openai_chat`, stored separately from the LocalAI and OpenAI templates. |
| `CUSTOM_RESPONSES_REQUEST_TEMPLATE` | The request template for `openai_responses` (`model` + `input`), stored independently from the other templates. |
| `CUSTOM_MESSAGES_REQUEST_TEMPLATE` | The request template for `anthropic_messages` (`system` separated from `messages`, required `max_tokens`), stored independently from the other templates. |
| `AI_PROMPT`                     | The prompt template for AI-based translation services.                          |
| `AI_USER_PROMPT`                | The user message template. Supports `{lineToTranslate}`, `{contextBefore}` and `{contextAfter}`. |

Each of these variables also accepts the `_FILE` variant described under **Docker secrets**.

#### **Proofreading**

Proofreading is only available for the AI services above (OpenAI, Anthropic, Gemini, DeepSeek, Mistral, xAI, LocalAI, Custom), and only for a `service_type` entry that supports it. It uses two more settings, shared across all AI services the same way `AI_PROMPT` and `AI_USER_PROMPT` are. These are configured through **Settings > Services > Request Settings** in the web interface, or through their environment variables:

| **Setting** | **Environment Variable** | **Default** |
|-------------|--------------------------|-------------|
| `proofread_prompt` | `PROOFREAD_PROMPT` | `You are proofreading a subtitle translated from {sourceLanguage} to {targetLanguage}. Compare the translation against the source and correct mistranslations, wrong names, grammar and punctuation, preserving the tone and meaning without censoring the content. Keep the length close to the original so it still fits on screen. If the translation is already correct, repeat it unchanged. Provide only the corrected translation as output, with no additional comments.` |
| `proofread_user_prompt` | `PROOFREAD_USER_PROMPT` | `[SOURCE] {sourceLine}\n[TRANSLATION] {translatedLine}` |

See the AI services documentation for the full placeholder list.

#### **AI request settings**

Shared across OpenAI, Anthropic, Gemini, DeepSeek, Mistral, xAI, LocalAI and Custom. Configure them under **Settings > Services > Request Settings**, or through environment variables. Do not add these rows to every provider table.

| **Setting** | **Environment Variable** | **Default** | **Description** |
|-------------|--------------------------|-------------|-----------------|
| `ai_max_tokens` | `AI_MAX_TOKENS` | `0` | Maximum tokens for one completion (thinking plus the answer). `0` (the default) omits the field so the provider or request-template default is kept. OpenAI receives `max_completion_tokens`; LocalAI, other chat providers, official Anthropic and Custom over Messages receive `max_tokens`; Custom over Responses receives `max_output_tokens`; Gemini receives `generationConfig.maxOutputTokens`. Anthropic and Custom Messages keep the template default of `8192` when the field is omitted; Custom Messages still requires a positive `max_tokens`. Not applied to LocalAI generate (Ollama `/api/generate`). |
| `ai_thinking` | `AI_THINKING` | `auto` | Chain-of-thought control for OpenAI-compatible chat only (LocalAI chat and Custom over `openai_chat`). `auto` (the default) sends no `thinking` field. `off` writes `thinking.disabled` (needed for OpenCode Go / MiMo). `on` writes `thinking.enabled`. Responses and Anthropic Messages have no `thinking` field and are left untouched. |

#### **DeepL**

| **Environment Variable** | **Description**                                                                 |
|--------------------------|---------------------------------------------------------------------------------|
| `DEEPL_API_KEY`          | The API key for authenticating with DeepL.                                      |

---

### **Translation Services**

The `SERVICE_TYPE` variable determines which translation services to use. Provide a JSON array, 
the first entry is the primary service and any remaining entries are fallbacks, tried in order when the primary fails. 
A single value is also accepted and is rewritten to an array on startup.

#### Example:
```json
["openai","anthropic","libretranslate"]
```

An entry may also be an object with a `provider` and an optional `model`, so several models of one provider can be tried in order before the chain moves on to the next service. The model applies to that entry only and does not change the provider's default model:

```json
[{"provider":"custom","model":"model-a"},{"provider":"custom","model":"model-b"},{"provider":"deepl"}]
```

Fallback behaviour is the same in the web UI and when `SERVICE_TYPE` is set from the environment:

- Language matching is applied before list order. A later row that matches the requested pair exactly is tried before an earlier row that only matches through an alias or a script/neutral equivalent. Within the same match tier, the configured order is used.
- Rows of the same provider share that provider's address, API key, protocol, default model and request template. Only the model override on each row is independent.
- A rate limit, request timeout (`408` or the configured request timeout, default 5 minutes), or gateway error (`502`, `503`, `504`) is retried on the current candidate, then the next entry is tried.
- HTTP `401` (or a missing address / API key) skips the remaining models of that provider.
- With batch translation enabled, a failed batch candidate does not fail the file: unfinished lines continue down the chain, including candidates that can only translate line by line.

Existing `localai` configurations keep working; Custom is opt-in and is not migrated from LocalAI. The object-array form written by this version cannot be read by older Lingarr releases that only understand a string or a string array.

The supported values are:

| **Value**       | **Description**                                                                 |
|-----------------|---------------------------------------------------------------------------------|
| `libretranslate`| Use the LibreTranslate service for translations.                                |
| `openai`        | Use OpenAI's models (e.g., GPT-4, GPT-3.5) for translations.                    |
| `anthropic`     | Use Anthropic's models (e.g., Claude-2) for translations.                       |
| `gemini`        | Use Google's Gemini models for translations.                                    |
| `deepseek`      | Use DeepSeek's models for translations.                                         |
| `mistral`       | Use Mistral's models for translations.                                          |
| `xai`           | Use xAI's Grok models for translations.                                         |
| `localai`       | Use a locally hosted or OpenAI-compatible AI model for translations.            |
| `custom`        | Use your own gateway over Chat Completions, Responses, or Anthropic Messages. Independent from LocalAI. |
| `deepl`         | Use the DeepL API for translations.                                             |
| `google`        | Use Google Translate for translations.                                          |
| `bing`          | Use Bing Translate for translations.                                            |
| `microsoft`     | Use Microsoft Translator for translations.                                      |
| `yandex`        | Use Yandex Translate for translations.                                          |
