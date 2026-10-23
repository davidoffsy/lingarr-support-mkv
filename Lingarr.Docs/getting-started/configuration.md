# Configuration

Lingarr is primarily configured through its web interface. However, every setting can also be provided as an environment variable.

## General

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `ASPNETCORE_URLS=http://+:9876` | The internal port that Lingarr will listen on inside the container. |
| `BASE_PATH` | Optional URL prefix to host Lingarr under a sub-path (e.g., behind a reverse proxy). Example: `/lingarr`. Leave unset to serve from the root. |
| `MAX_CONCURRENT_JOBS=1` | Sets the amount of jobs that can run concurrently, defaults to 1. |
| `JOB_TIMEOUT_MINUTES=30` | Minutes of silence before Hangfire retries a job. `sqlite` only, defaults to `30`. The effective SQLite timeout is `max(JOB_TIMEOUT_MINUTES, EXTRACT_TIMEOUT_MINUTES)` (extract default `60`), so a default install is not reclaimed mid-extract. |
| `TZ=Europe/Amsterdam` | The time zone Lingarr runs in, defaults to UTC. It applies to schedules, log timestamps and every date shown in the web interface, so set it to your own zone. A schedule of `0 3 * * *` runs at 3am in this zone. |
| `FFMPEG_PATH` | Optional path to the `ffmpeg` binary. Defaults to `ffmpeg` on `PATH`. The Docker image already includes FFmpeg. |
| `FFPROBE_PATH` | Optional path to the `ffprobe` binary. Defaults to `ffprobe` on `PATH`. The Docker image already includes ffprobe. |

## Database

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `DB_CONNECTION=mysql` | Specifies the database connection type. Options are `mysql`, `postgresql` or `sqlite`. |
| `DB_HOST=Lingarr.Mysql` | The hostname for the database (required when using `mysql` or `postgresql`). |
| `DB_PORT=3306` | The port for the database (required when using `mysql` or `postgresql`). |
| `DB_DATABASE=Lingarr` | The name of the database (required when using `mysql` or `postgresql`). |
| `DB_USERNAME=Lingarr` | The username for the database (required when using `mysql` or `postgresql`). |
| `DB_PASSWORD=Secret1234` | The password for the database (required when using `mysql` or `postgresql`). |
| `SQLITE_DB_PATH` | Path to the SQLite database file used when `DB_CONNECTION` is `sqlite`. Defaults to `local.db` within `/app/config/`. |
| `DB_HANGFIRE_SQLITE_PATH=/app/config/Hangfire.db` | Path to the SQLite database file used by the Hangfire job store. Defaults to `/app/config/Hangfire.db`. |

## Docker secrets

Any environment variable can also be supplied by appending `_FILE` to the variable name and pointing it at a file containing the value. When both `<VAR>` and `<VAR>_FILE` are set, `<VAR>_FILE` will be selected primarily.

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

# Optional settings

The following settings are all **optional** and can be provided via environment variables.

## Radarr and Sonarr

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `RADARR_URL` | The base URL for the Radarr API. Example: `http://localhost:7878`. |
| `RADARR_API_KEY` | The API key for authenticating with Radarr. Obtain this from your Radarr instance. |
| `SONARR_URL` | The base URL for the Sonarr API. Example: `http://localhost:8989`. |
| `SONARR_API_KEY` | The API key for authenticating with Sonarr. Obtain this from your Sonarr instance. |

Webhook URLs are shown at **Settings → Integrations**. Generate a secret there, then copy the full URL, including the Lingarr-generated `token=` query. Do **not** put Lingarr's `api_key` or the Radarr/Sonarr API keys into the webhook URL. Existing connections without a token keep working until you generate a secret.

## Plex

Connect Plex from the web UI: **Settings → Integrations → Sign in with Plex**. Plex Web has no copyable API token; do **not** scrape XML or `X-Plex-Token`.

These environment variables are an optional **headless / reinstall fallback** only. Prefer Sign in with Plex.

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `PLEX_URL` | The Plex Media Server base URL, with no trailing slash. Example: `http://plex:32400`. |
| `PLEX_TOKEN` | plex.tv access token from Sign in with Plex (sensitive). Prefer UI OAuth; use this only for automation installs. |

Do **not** put `PLEX_TOKEN` or Lingarr's `api_key` into the Plex Webhooks URL. The webhook secret is generated in the UI and appears only as the query `token=` on `POST /api/webhook/plex`.

Keep **Remove language tag** off so Plex can pick `{stem}.{lang}.srt`. If Plex paths differ from Radarr/Sonarr, add Movie / Show mappings at `/settings/mapping`. Five-star ratings work for movies, episodes, and seasons. Titles that are not in Radarr/Sonarr still translate when Lingarr can read the mapped file; they are not added to the Movies or Shows lists. See [Plex](/getting-started/plex) for the full five-star setup guide.

## Authentication

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `AUTH_ENABLED` | Enables or disables authentication. |
| `ENCRYPTION_KEYS` | The path for storing encryption keys, stored by default in the config directory. |

## Telemetry

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `TELEMETRY_ENABLED` | Opt-in for anonymized telemetry to help other Lingarr users, defaults to false. |

When enabled, Lingarr submits a report at most once every 7 days, Each report also carries an installation identifier.
This is a random value generated once on first submission and stored locally as a setting and doesn't persist on reinstallations.

## Translation services

| **Environment Variable** | **Description** |
|--------------------------|-----------------|
| `SERVICE_TYPE` | An ordered JSON array of translation services. Each entry is a provider name or an object `{ "provider", "model?" }`. Language support is matched first; within the same match, entries are tried in order. A single value (e.g. `openai`) is still accepted and is promoted to `[{"provider":"openai"}]` on startup. The object form is not readable by older Lingarr versions. |
| `SOURCE_LANGUAGES` | A minified JSON array of source languages for translation. |
| `TARGET_LANGUAGES` | A minified JSON array of target languages for translation. |
| `USE_BATCH_TRANSLATION` | Set to `true` to translate subtitles in batches with AI services, defaults to false. |
| `SCAN_EMBEDDED_SUBTITLES` | Set to `true` to probe MKV text tracks (SRT / ASS / SSA) during automatic and bulk translation. Movie and show lists are never probed. Defaults to true for new installations; existing settings are preserved. |
| `EXTRACT_EMBEDDED_SOURCE` | Set to `true` to write the selected source track as a sidecar next to the video so Plex / Emby can switch languages. Defaults to true. Translation of embedded tracks always extracts this file first. |
| `EXTRACT_TIMEOUT_MINUTES` | Minutes ffmpeg may spend copying an embedded text track **on demand** (the MKV button). Defaults to `60`. Automatic / bulk extract still uses a 2-minute timeout so a cloud remux cannot stall the queue. Cancel the request if a remux is still too slow. |

The `SOURCE_LANGUAGES` and `TARGET_LANGUAGES` variables should be provided as a minified JSON array. Each object in the array should contain the `name` and [ISO 639](https://en.wikipedia.org/wiki/List_of_ISO_639_language_codes) or [ISO 639-2](https://en.wikipedia.org/wiki/ISO_639-2) language codes:

```json
[{"name":"English","code":"en"},{"name":"Dutch","code":"nl"}]
```

`SERVICE_TYPE` example, with OpenAI as the primary service and Anthropic and LibreTranslate as fallbacks:

```json
["openai","anthropic","libretranslate"]
```

An entry may also carry its own model, so several models of the same provider can be tried in order before the chain moves on to the next service. The model applies to that entry only and does not change the provider's default model:

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

- **[AI services](/translation-services/ai-services)**: [`openai`](/translation-services/ai-services#openai), [`anthropic`](/translation-services/ai-services#anthropic), [`gemini`](/translation-services/ai-services#gemini), [`deepseek`](/translation-services/ai-services#deepseek), [`mistral`](/translation-services/ai-services#mistral), [`xai`](/translation-services/ai-services#xai), [`localai`](/translation-services/ai-services#localai) and [`custom`](/translation-services/ai-services#custom)
- **[Machine translation](/translation-services/machine-translation)**: [`libretranslate`](/translation-services/machine-translation#libretranslate), [`deepl`](/translation-services/machine-translation#deepl), [`google`](/translation-services/machine-translation#google-bing-microsoft-and-yandex), [`bing`](/translation-services/machine-translation#google-bing-microsoft-and-yandex), [`microsoft`](/translation-services/machine-translation#google-bing-microsoft-and-yandex) and [`yandex`](/translation-services/machine-translation#google-bing-microsoft-and-yandex)

Each service has its own configuration variables, such as API keys and model selection, documented on its settings page.
