# Plex

Rate a movie or episode **five stars** in Plex and Lingarr enqueues a translation. This requires [Plex Pass](https://www.plex.tv/plex-pass/) webhooks (`media.rate` at 5 stars / 10/10).

Only titles **already synced from Radarr or Sonarr** are translated. Orphan Plex-only titles are skipped; Lingarr does not create new movie or episode rows for them.

## Setup

1. Configure source and target languages. Keep **Remove language tag** off (`remove_language_tag=false`, Settings → Subtitle) so Plex can pick `{stem}.{lang}.srt`.
2. Open **Settings → Integrations → Plex** and choose **Sign in with Plex**. Authorize in the browser, then pick an owned server. In Docker, if the LAN IP fails, set **Server address** to `http://plex:32400` (container DNS). Use **Test connection**. The settings page never shows the account token.
3. Copy the **Webhook URL** (it includes a Lingarr-generated secret) into **Plex Web → account → Webhooks**. That only registers the URL. On the **server** (wrench icon, not the avatar): **Settings → Network → Show Advanced → Webhooks** must be on, and **Settings → General → Push Notifications** should be on. Restart PMS after changing these. The Plex Media Server process must be able to reach Lingarr. The same Docker network is enough; a public URL is not required. A GET/curl of the Lingarr homepage does **not** count — Plex **POSTs** `multipart/form-data` with a `payload` field. If a reverse proxy sits in front, allow POST and a body of at least a few MB (`client_max_body_size`); Plex attaches a thumbnail. After a saved five-star rating, use **Check last webhook** on Settings → Plex. If it stays empty, PMS is not sending (Plex Pass, webhook URL, or nginx rejecting the POST body).
4. Add **Path Mapping** at `/settings/mapping` if the Plex `Part.file` disk or prefix differs from Arr/Lingarr. Use a **Movie** mapping for films and a **Show** mapping for episodes. Lingarr must be able to **read** those video files (mount the same volumes).
5. **Who can trigger** defaults to **Home users** (the server owner plus Plex Home managed accounts on TVs). Remote friends do not trigger translation unless you set this to **Anyone**.

## Do not

- Paste Lingarr's API key or a plex.tv token into Plex Webhooks.
- Look for a token in Plex settings. Plex Web has no copyable API token; use **Sign in with Plex** instead of scraping XML or `X-Plex-Token`.

The webhook secret is generated in Lingarr and appears only as the query `token=` on `POST /api/webhook/plex`.

## Behaviour

- Sidecar subtitles are preferred. If none match, Lingarr uses an MKV text track (not Forced or SDH). Extraction runs on the translation queue, not the webhook worker — a cloud remux can be slow.
- Five-star ratings still probe MKV even if **Scan embedded subtitles** is off, unless a full target-language sidecar already exists.
- Translation is skipped when a full target-language subtitle already exists as a sidecar or inside the MKV (text or PGS). Forced-only tracks do not count. SDH / HI / CC tracks count as present. Region codes such as zh-CN match a zh track.
- Titles that are not in Lingarr's Arr library are skipped (no new movie rows).

## Prerequisites

1. **Sign in with Plex** first. Do not look for a token in Plex.
2. The title must already be synced from Radarr or Sonarr into Lingarr.
3. Path mapping must turn Plex `Part.file` into Lingarr's `Path` + `FileName` (Movie mapping for films, Show mapping for episodes).
4. The Lingarr container must mount those video directories so it can read the files.
5. `source_languages` and `target_languages` must be configured.
6. The webhook URL must point at **Lingarr**, and the Plex Media Server process must be able to reach it.
7. Do not put Lingarr's API key or a plex.tv token into Plex Webhooks.
8. Keep `remove_language_tag=false`.

## Manual checklist

1. **Settings → Integrations → Plex → Sign in with Plex.** Authorize in the browser, then pick a server. In Docker, if the connection fails, change **Server address** to the container hostname (for example `http://plex:32400`). **Test connection** succeeds. The settings page never shows the account token.
2. Copy the webhook URL into **Plex Web → account → Webhooks**.
3. Rate a movie that is **already in the Lingarr list** five stars. A job appears under Translations; when it finishes, `{stem}.{lang}.srt` sits next to the video.
4. Rate the same title five stars again. No new request is created (already Completed).
5. Rate a synced episode five stars. It is enqueued the same way.
6. Rate four stars or clear the rating. Nothing is enqueued.
7. Rate five stars on a title that exists in Plex but not in Lingarr. Logs show a skip; no new Movie row and no translation job.
8. Use an Arr title that only has embedded English in the MKV. The job shows extract progress (cloud storage can take a long time). The webhook queue must not stall.
9. With **Scan embedded subtitles** off, five-star still translates embedded tracks (decoupled from automatic scanning).
10. If the MKV already has a full target-language track (including PGS), five-star does not enqueue. A forced-only target track still translates. SDH counts as done. zh-CN matches a zh track.
11. After **Disconnect**, five-star can no longer resolve paths (logs say unauthorized). The webhook secret still validates inbound requests.
12. From a Plex Home **managed account** (the profile signed in on a TV), rate a synced movie five stars. It is enqueued. The default **Home users** setting must not block this.
