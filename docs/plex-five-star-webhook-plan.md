# Plex 五星 Webhook → 翻译 一期实施方案

状态：一期已实施  
后续：孤儿片路径翻译 + 整季五星见 `docs/plex-orphan-five-star-path-translation-plan.md`  
前置：MKV 抽轨 / `embedded:{path}:{index}` 占位、`TranslationJob` 抽轨、Radarr/Sonarr webhook 已可用  
范围：Plex Pass `media.rate`（五星）触发，仅翻译 **已从 Radarr/Sonarr 同步进 Lingarr** 的电影和剧集  
非目标（一期）：孤儿片（Plex 有、Arr 无；已另案实施）、无 Pass 轮询、Tautulli、译后 Plex 扫库、图像字幕 OCR、回封装 MKV

---

## 1. 背景

Lingarr 片库只来自 Radarr / Sonarr。翻译、MKV 文本轨抽出、sidecar 命名已经齐备，但没有任何 Plex 客户端。

目标体验：在 Plex 里给电影或单集点 **5 星**，Lingarr 按设置里的源/目标语言入队翻译。外挂优先，没有再抽 MKV。

约束（已拍板）：

| 项 | 一期约定 |
|---|---|
| 目标语言 | 用设置 `target_languages`，请求体不带语言 |
| 源字幕 | 外挂 sidecar 优先；没有再 MKV 文本轨 |
| 多条英文字幕 | Plex **翻译场景**排序（见 §5），不读播放器 `selected=1` |
| 媒体范围 | 电影 + 剧集；必须已在 Lingarr（Arr 同步过） |
| 触发 | Plex Pass webhook `media.rate` |
| 接 Plex | **Sign in with Plex**（PIN/OAuth）。Plex 设置里没有 API Token，禁止手填 |
| 路径 | 复用现有 Path Mapping（电影 `Movie`，剧集 `Show`） |
| 孤儿片 | **不做**。对不上 Arr 记录就记日志并跳过 |
| 抽轨 | 不在 webhook 工人里抽（2 分钟超时会失败且堵住 Arr webhook） |

Plex webhook **通常不带** `Part.file`。路径一律用 `ratingKey` 再打 Plex API，不要换 `media.play` 当触发器。

---

## 2. 产品流程

```
Plex 点 5 星
  → PMS multipart POST /api/webhook/plex?token={webhook_secret}
  → 立刻 200，入 Hangfire queue "webhook"
  → 过滤 event / 分数 / 类型 / 评分账号（主账号 + Plex Home 托管用户）
  → GET {plex_url}/library/metadata/{ratingKey}  （X-Plex-Token 来自 Sign in）
  → PathConversionService 映射
  → 按 Path + FileName 查 Movie / Episode
  → 找不到再用 Guid（tmdb/tvdb/imdb）问 Radarr/Sonarr，走现有 Arr id 解析
  → 仍找不到：日志 "not in Lingarr library"，结束（不建孤儿片）
  → 扫 sidecar；无条件 probe MKV（不看 scan_embedded_subtitles）
  → 按 §5 选源
  → CreateRequest；内嵌源写 embedded:{mkv}:{index}，TranslationJob 抽轨
  → 目标语言来自设置；已有 sidecar / Pending|InProgress|Completed 则跳过
```

HTTP 必须在几十毫秒到一两秒内返回。Plex 不等翻译。

---

## 3. 做与不做

| 做 | 不做 |
|---|---|
| `POST /api/webhook/plex`（multipart + 纯 JSON） | 改 Radarr/Sonarr webhook 行为 |
| Sign in with Plex（服务端 PIN）+ 选服务器 + 可改地址 | 设置页「Plex Token」输入框 |
| 自生成 webhook secret，整段 URL 复制到 Plex Webhooks | 把主 `api_key` 或 plex.tv token 写进 Webhook URL |
| `ratingKey` → Plex metadata 补路径 | 依赖 webhook 自带 `Part.file` |
| 路径映射 Movie / Show | 新增 `MediaType.Plex` |
| Guid → Arr → 已有 `GetMovieIdOrSyncFromRadarrMovieId` 等 | 按标题模糊匹配（易译错片） |
| 五星无条件 probe MKV | 改自动任务 / bulk 的选轨 |
| 内嵌占位交给 `TranslationJob`（长超时） | webhook 工人里 `ExtractAsync`（2 min） |
| 电影 + 单集 | 剧 / 季 / 合集 / 音乐评分 |
| Integrations 页 Plex 卡片 | 新侧栏、Tautulli、轮询 |
| 对不上 Arr 则跳过 | 临时建 `RadarrId=0` 孤儿片 |
| | 译完调 Plex refresh（二期） |
| | 无 Plex Pass 的 API 轮询（二期） |
| | 浏览器直连 plex.tv 创建 PIN（CORS） |

---

## 4. 设置与鉴权

Plex Web **没有** Radarr/Sonarr 那种可复制的 API Key。Token 藏在 XML / 已授权设备里，让用户手填是错的。

Sonarr、Radarr、Overseerr、Tautulli 接 Plex 都是 **Sign in with Plex**（plex.tv PIN）。一期照这个做：用户点按钮，浏览器打开 Plex 授权，Lingarr **服务端**拿到 token 并加密保存。界面不出现 Token 输入框。

这里有两套完全不同的秘密，不要混：

| | 从哪来 | 干什么 | 用户看不看得到 |
|---|---|---|---|
| `plex_token` | Sign in with Plex | Lingarr → PMS：补 `Part.file` | 不展示、不回显 |
| `plex_webhook_secret` | Lingarr 自己生成 | PMS → Lingarr，URL 上的 query | 只作为整段 webhook URL 的一部分复制 |

Plex 的 Webhooks 设置也只有一个 URL 框，不能加 Header，所以 webhook 只能把 Lingarr 自己的 secret 放在 query 里。**不要**把 plex.tv token 填进这条 URL。

### 4.1 Sign in with Plex（PIN，服务端完成）

浏览器直连 `plex.tv` 会踩 CORS（Radarr 的 Start OAuth 也遇到过）。PIN 的创建和轮询必须走 Lingarr 后端。

稳定 `X-Plex-Client-Identifier`：第一次授权时生成 UUID，存 `plex_client_identifier`，之后一直用。`X-Plex-Product: Lingarr`。

```
用户点「Sign in with Plex」
  → POST /api/plex/auth/pin            （需已登录 Lingarr）
  → 后端 POST https://plex.tv/api/v2/pins  strong=true
  → 返回 { pinId, code, authUrl }
  → 前端 window.open(authUrl)
       https://app.plex.tv/auth#?clientID={uuid}&code={code}
         &context[device][product]=Lingarr
  → 前端每 1–2s GET /api/plex/auth/pin/{pinId}
  → 后端 GET https://plex.tv/api/v2/pins/{id}
  → authToken 非空：加密写入 plex_token，顺带写入 plex_account_title
  → GET https://plex.tv/api/v2/resources?includeHttps=1
       过滤 provides=server 且 owned
  → 前端下列表，用户选一台
  → 写入 plex_machine_id + plex_url
```

不采用 `forwardUrl` 回调：Lingarr 经常在 Docker / 局域网，没有公网回跳地址。轮询和 Radarr「Start OAuth」一样，用户浏览器能打开 plex.tv 即可。

PIN 约 15 分钟过期。超时或关掉弹窗：前端停轮询，不写 token。

**选服务器 / 地址覆盖**

plex.tv 返回的 `connections[].uri` 往往是宿主机 LAN IP。Lingarr 若在 Docker 里，这个地址可能不通，需要改成 `http://plex:32400`。

UI：

1. 未登录：按钮 **Sign in with Plex**
2. 已登录：Plex 用户名、**Disconnect**（清 token / machine id / account title，保留 webhook secret 和 client identifier）
3. 服务器下拉（owned servers）
4. **Server address** 可改（默认用选中连接的 local 非 relay uri，去尾 `/`）
5. **Test connection**：`GET {plex_url}/identity`，头带已存的 `X-Plex-Token`

`plex_settings_completed`：已有 token 且 `plex_url` 非空。

### 4.2 新键

`SettingKeys.Integration` 增加：

| Key | 存储 | 默认 | 说明 |
|---|---|---|---|
| `plex_url` | 明文 | `""` | PMS 基址，去尾 `/`。OAuth 后可改，给 Docker 用 |
| `plex_token` | 加密 | `""` | plex.tv Access Token。只由 PIN 流程写入 |
| `plex_client_identifier` | 明文 | `""` | 首次 PIN 生成的 UUID |
| `plex_machine_id` | 明文 | `""` | 选中服务器的 `clientIdentifier` |
| `plex_account_title` | 明文 | `""` | 授权账号名，设置页展示 |
| `plex_webhook_secret` | 加密 | `""` | 仅 webhook URL query，和 plex.tv token 分开 |
| `plex_min_rating` | 明文 | `"10"` | Plex 10 分制，5 星 = 10 |
| `plex_allowed_raters` | 明文 | `"home"` | `home`：主账号 + Plex Home 托管用户；`anyone`：含远程分享好友。**不要**用 webhook 的 `owner` 布尔值 |
| `plex_settings_completed` | 明文 | `"false"` | token + url 都有则 true |

`SettingService.SetSetting` 找不到 key 会直接失败，必须种子。下一号迁移：**`M0027_SeedPlexSettings.cs`**。

对前端暴露设置时：`plex_token` 只返回是否已授权（布尔 / 空串），**永不回显 token**。

可选环境变量仅作 **无 UI / 自动化安装兜底**，不是设置页的主路径：`PLEX_URL`、`PLEX_TOKEN`。文档写明优先 Sign in with Plex。

PIN 进行中的 `pinId` 只放内存或短 TTL 缓存，不入库。

### 4.3 Webhook 鉴权（PMS → Lingarr）

Plex **不能**自定义 Header。

`POST /api/webhook/plex`：

- `[AllowAnonymous]`，**不要**改全局 `LingarrAuthorizeAttribute`
- 未完成 onboarding → 403
- `auth_enabled=false` 时仍校验 `plex_webhook_secret`（避免局域网谁都能触发翻译）
- Query 名用 `token`，与 `plex_webhook_secret` 比较。**不要**用 `plex_token` 当 query 名，避免和账号 token 混淆
- secret 为空 → 401，日志说明去设置页生成
- 打开 Plex 卡片或首次保存时若 secret 为空，后端生成随机串（和 API key 生成同类）

Radarr/Sonarr 继续走 `X-Api-Key`，不动。

### 4.4 前端

新组件 `Lingarr.Client/src/components/features/settings/PlexSettings.vue`，挂到 `IntegrationPage.vue`。

- **Sign in with Plex** / 已授权状态（用户名 + Disconnect）
- 服务器下拉 + Server address（可编辑）
- Test connection
- Webhook URL：只读整段 `{origin}{base}/api/webhook/plex?token={secret}`，复制 + 重新生成 secret
- Min rating（默认 10）
- Who can trigger：默认 **家庭用户**（主账号 + 各电视上的托管账户）；可选「所有评分」（远程分享好友也会入队）
- 说明：Plex Web → 账号 → Webhooks，把上面整段 URL 贴进去；PMS 能访问 Lingarr 即可（同 Docker 网络），不必公网
- 链到 `/settings/mapping`：Plex 盘符和 Arr 不一致时在此加 Movie / Show 映射

不要：Token 密码框、让用户去 XML 里抄 `X-Plex-Token` 的文案。

`WebhookInstructions.vue` 不放 Plex URL（secret 和 Arr 的公开路径性质不同），只放在 Plex 卡片。

`setting.ts`：明文键进 `SETTINGS`；`ENCRYPTED_SETTINGS.PLEX_TOKEN` / `PLEX_WEBHOOK_SECRET` 只给 store 内部，UI 不绑 input。  
`store/setting.ts` 保存前对 `plex_url` 去尾 `/`。

新 API（均 `[LingarrAuthorize]`，要 Lingarr 登录，**不是** Plex webhook 匿名接口）：

| 方法 | 路由 | 作用 |
|---|---|---|
| POST | `/api/plex/auth/pin` | 创建 PIN，返回 `authUrl` |
| GET | `/api/plex/auth/pin/{id}` | 轮询；成功则落库 token |
| DELETE | `/api/plex/auth` | Disconnect |
| GET | `/api/plex/servers` | 已授权后列 owned servers |
| POST | `/api/plex/test` | 测当前 `plex_url` + 已存 token |

---

## 5. 选源（仅 Plex 触发）

播放器「当前选中轨」经常是 Forced，不适合当翻译源。一期 **不读** Plex `Stream.selected`。

Plex 触发专用排序（**不改** `SelectSourceSubtitle` / 自动任务 / bulk）：

1. 语言必须在 `source_languages` 里
2. 同语言：sidecar 永远压过内嵌（已有 `EmbeddedSubtitleMerger`）
3. 同语言多条：**非 Forced > 非 SDH/HI/CC > 较小轨号 / 文件顺序**
4. **忽略** MKV Default 旗标（Plex 播放也不看 Default）
5. `ignore_captions=true` 时：先空 caption，否则退回第 3 步

`und` / `unknown` 轨不自动选（与自动任务一致）。

五星是人点的：**始终 probe MKV**，即使 `scan_embedded_subtitles=false`。

内嵌源不要在 webhook 工人里抽出。`CreateRequest` 的 `SubtitlePath` 用已有占位：

```
embedded:{mkvPath}:{trackIndex}
```

`TranslationJob` 识别前缀后按 `extract_timeout_minutes`（默认 60）抽轨。sidecar 源直接传真实路径。

目标语言：设置 `target_languages` 里尚未存在 sidecar、且没有 Pending/InProgress/Completed 请求的语言。去重跟 `MediaSubtitleProcessor` 一样，避免连点五星刷任务。

---

## 6. 媒体解析（只认 Arr 库）

### 6.1 入口过滤

立刻 200 的条件之外，job 内跳过（打 Information，不当失败重试）：

- `event != media.rate`
- 分数：顶层 `rating`，否则 `Metadata.userRating`；`< plex_min_rating` 则跳过（清星、四星不译）
- `Metadata.type` 不是 `movie` / `episode`
- 评分账号不在允许范围内（见 §6.1.1）

分数解析要兼容 `10`、`10.0`、字符串 `"10"`。

### 6.1.1 谁点的星：主账号 + 托管账户，默认都触发

客厅电视常用 **Plex Home 托管账户**（managed / restricted user），一人一台。这些账号点五星必须能入队。

**不要**用 payload 顶层的 `owner`：

官方含义是「这条 webhook 是因为**服务器主账号配置了 webhook** 才发出」，不是「点星的人是服务器主账号」。主账号自己点星时 `Account.id` 恒为 `1`；托管用户点星时 `owner` 仍可能是 `true`（走的是主账号那条 webhook）。用 `owner != true` 既拦不住好友，也可能误伤托管账户。

谁点的星看 `Account.id` / `Account.title`。

默认 `plex_allowed_raters=home`：

1. `Account.id == 1` → 服务器主账号，放行
2. 否则对照 Plex Home 用户列表（标题或 id 命中）→ 托管账户，放行
3. 对不上 → 当作远程分享好友，跳过（避免别人片库里点星也烧翻译额度）

拉 Home 用户（授权后已有 token）：

```
GET https://plex.tv/api/v2/home/users
Accept: application/json
X-Plex-Token: {plex_token}
```

缓存 30–60 分钟。plex.tv 暂时失败时 **放行**（宁可多译，不要把客厅电视卡住），打 Warning。

`plex_allowed_raters=anyone`：不做账号过滤。

设置文案不要写「仅 owner / 分享用户不触发」，容易把托管账户理解成分享用户。

### 6.2 补路径

`PlexApiClient.GetMetadata(ratingKey)` 使用 **Sign in 写入的** `plex_token`，不是 webhook secret：

```
GET {plex_url}/library/metadata/{ratingKey}
Accept: application/json
X-Plex-Token: {plex_token}
```

取：

- 第一个（或 `selected=true` 的）`Media[].Part[].file`
- `Guid[].id`（`tmdb://` / `tvdb://` / `imdb://`）
- `type` / `title` / `year` / 剧集的 `grandparentTitle`、`parentIndex`、`index`

webhook 里若偶尔自带 `Part.file`，可以先用，仍建议有 token 时拉一次 Guid。  
未 Sign in（`plex_token` 空）且 webhook 无路径 → 跳过并说明要先 Sign in with Plex。

### 6.3 对上 Lingarr

**主路径：文件路径**

1. `ConvertAndMapPath(file, Movie)` 或剧集用 `Show`（与 `EpisodeSync` 一致）
2. `Path = GetDirectoryName`，`FileName = GetFileNameWithoutExtension`
3. `Movies`：`Path == dir && FileName == stem`
4. `Episodes`：同样；注意 bulk 有时用 `Season.Path`，webhook/同步用 `Episode.Path`。查找应对 **Episode.Path 或 Season.Path** 都能命中

**兜底：Guid → Arr（仍是 Arr 片，不是孤儿）**

- 电影 `tmdb://` / `imdb://` → Radarr 查 id → `GetMovieIdOrSyncFromRadarrMovieId`
- 剧集 `tvdb://` + S/E → Sonarr 查 episode id → `GetEpisodeIdOrSyncFromSonarrEpisodeId`

**不做：** 标题 `Contains`。一期宁可漏，不要译错片。

对不上：`LogInformation`（ratingKey、title、mapped path），return。不写 Movie/Episode 行。

---

## 7. 模块划分

禁止把解析、Plex HTTP、选源、PIN 全塞进 `WebhookController`。

| 新文件 | 职责 |
|---|---|
| `Lingarr.Server/Models/Webhooks/PlexWebhookPayload.cs` | 混合大小写 DTO：`event`、`rating`、`owner`、`Metadata` |
| `Lingarr.Server/Services/Plex/PlexWebhookParser.cs` | multipart 字段 `payload`；也接受 `application/json` |
| `Lingarr.Server/Services/Plex/PlexTvClient.cs` | plex.tv：PIN、resources、home users |
| `Lingarr.Server/Services/Plex/PlexRaterFilter.cs` | `Account` ↔ Home 用户；不读 webhook `owner` |
| `Lingarr.Server/Services/Plex/PlexApiClient.cs` | PMS：metadata / identity，超时 5–10s |
| `Lingarr.Server/Services/Plex/PlexAuthService.cs` | PIN 会话、落库 token、Disconnect |
| `Lingarr.Server/Controllers/PlexAuthController.cs` | `/api/plex/auth/*`、`servers`、`test` |
| `Lingarr.Server/Services/Plex/PlexMediaResolver.cs` | 映射 + DB + Guid/Arr |
| `Lingarr.Server/Services/Plex/PlexSourceSubtitleSelector.cs` | §5 排序 |
| `Lingarr.Server/Services/Plex/PlexRatingTrigger.cs` | 过滤 → 解析 → 选源 → `CreateRequest` |
| `Lingarr.Server.Tests/.../Plex/*` | PIN 解析、webhook 过滤、映射、选源、跳过孤儿 |

现有改动（尽量小）：

| 文件 | 动作 |
|---|---|
| `WebhookController.cs` | `POST plex`：AllowAnonymous + 校验 **webhook secret** + Enqueue |
| `WebhookJob.cs` | `ProcessPlexWebhook`；**禁止**在此 Extract |
| `SettingKeys.cs` | Integration 新键 |
| `M0027_SeedPlexSettings.cs` | 种子 |
| `StartupService.cs` | 可选 env 兜底 |
| `Program.cs` / DI | `HttpClient` named `"PlexTv"`、`"Plex"` |
| `TranslationRequestService.CreateRequest` | 已支持 `embedded:` 则不动 |
| `MediaSubtitleProcessor` | **不改默认行为**；Plex 走自己的 Trigger |
| `PathConversionService` | 复用，不改 |
| `IntegrationPage.vue` / `PlexSettings.vue` / `setting.ts` | UI |
| `Settings.MD` + `Lingarr.Docs/getting-started/configuration.md` | 写 Sign in with Plex；env 仅兜底 |

JSON：`PropertyNameCaseInsensitive = true`。Plex 是 `event` 小写 + `Metadata`/`Account` 首字母大写。

忽略 multipart 里的 JPEG，不要读进内存当业务数据。注意表单体积限制，海报可能导致默认 form 限制；应只读 `payload` 字符串部分。

---

## 8. `WebhookJob` 注意

现有：

```csharp
[DisableConcurrentExecution(timeoutInSeconds: 2 * 60)]
[AutomaticRetry(Attempts = 3)]
[Queue("webhook")]
```

`ProcessPlexWebhook` 必须只做：Plex GET + DB + CreateRequest。抽轨/翻译在 `translation` 队列。

Plex GET 失败（超时、401）：记 Error，让 Hangfire 重试 3 次。  
「不是五星 / 不是 movie / 找不到 Arr 片」：成功结束，不 throw。

并发：同一 `ratingKey` 连点，靠 `CreateRequest` / Completed 去重，不必再加分布式锁。

---

## 9. 分阶段

### Phase A — 设置、OAuth、空 webhook（约 1 天）

- [ ] `M0027` 种子 + `SettingKeys`
- [ ] `PlexTvClient` + `PlexAuthService` + `/api/plex/auth/pin` 轮询落库
- [ ] 前端 Sign in with Plex / Disconnect / 服务器列表 / 可改 URL / Test connection
- [ ] token 不回显
- [ ] 生成 webhook secret，展示可复制 URL
- [ ] `POST /api/webhook/plex` 收 multipart / JSON，校验 webhook secret，立刻 200
- [ ] 非 `media.rate`、低分、剧/季：入队后 no-op
- [ ] 评分账号：`Account.id=1` 与托管用户放行；好友在 `home` 模式下跳过；不读 webhook `owner`
- [ ] 单测：PIN 成功/过期、secret、过滤
- [ ] curl 纯 JSON 可测 webhook，不依赖真 Plex

### Phase B — 对上 Arr 媒体（约 1 天）

- [ ] `PlexApiClient` 用已存 token；假服务单测
- [ ] `PlexMediaResolver`：路径映射 Movie/Show；Episode.Path 或 Season.Path
- [ ] Guid → Radarr/Sonarr → 现有 Get*OrSync*
- [ ] 对不上不写库
- [ ] 未 Sign in、缺路径、Plex 404 的日志

### Phase C — 选源入队（约 1 天）

- [ ] 始终 probe MKV（locator + `ProbeAsync`）
- [ ] `PlexSourceSubtitleSelector` 单测：外挂压内嵌、非 forced、非 SDH、忽略 default
- [ ] sidecar → `CreateRequest` 真实路径
- [ ] 仅内嵌 → `embedded:{mkv}:{index}`
- [ ] 目标语言来自设置；已有目标 / 已有请求则跳过
- [ ] 不调用 `EnsureSidecarSource`（那是 2 min 抽轨）
- [ ] 手工：给已同步电影点五星，Translations 出现任务

### Phase D — 文档与验收（约 0.5 天）

- [x] `Settings.MD` / configuration.md：Sign in with Plex，不教抄 token
- [x] 说明 Mapping、`remove_language_tag` 应关闭
- [x] 验收清单 §11

---

## 10. 测试要点

| 用例 | 期望 |
|---|---|
| POST pin → 假 plex.tv 返回 code | 返回 authUrl，含 clientID 与 code |
| 轮询 PIN 直到 authToken | 加密写入，GET 设置看不到 token 明文 |
| PIN 过期 / 未授权 | `linked: false`，不写 token |
| Disconnect | 清 token，webhook secret 仍在 |
| multipart `payload` + JPEG | 只解析 JSON，200 |
| 纯 JSON `media.rate` rating=10 type=movie（主账号或托管） | 入队 |
| `media.play` / rating=6 / type=show | 200 且不建翻译 |
| webhook secret 错误或空 | 401 |
| `home` + `Account.id=1` | 放行（主账号） |
| `home` + Account 命中 Home 用户（客厅电视托管账户） | 放行，即使 payload `owner=false` |
| `home` + Account 不在 Home 列表 | 跳过（远程好友） |
| `anyone` + 任意 Account | 放行 |
| webhook `owner=true` 但 Account 是好友 | `home` 模式下仍跳过（不信 `owner` 标志） |
| webhook 无 file，metadata 有 `/mnt/plex/Foo/Foo.mkv`，mapping `/mnt/plex`→`/movies` | 命中 `Path=/movies/Foo` `FileName=Foo` |
| 剧集 mapping 用 `Show` | 命中 Episode |
| 路径 miss、Guid tmdb 能命中 Radarr | 走 Arr 同步/查找 |
| 路径 miss、Guid 也 miss | 不 insert Movie |
| `en.srt` + 内嵌 en | 选 sidecar |
| 仅内嵌 en default + en forced + en sdh | 选非 forced 非 sdh |
| 目标 zh sidecar 已在 | 不入队 |
| 内嵌源 | 请求路径以 `embedded:` 开头 |

集成测试 mock `IPlexTvClient` / `IPlexApiClient`，不必起 PMS。

---

## 11. 手工验收

前置：Radarr/Sonarr 已同步；Lingarr 能读视频目录；源/目标语言已配；`remove_language_tag=false`。

1. Integrations → Plex → **Sign in with Plex**，浏览器授权后选服务器；Docker 下如连不上则改 Server address 为容器名。Test connection 成功。设置页看不到 token。
2. 复制 webhook URL 到 Plex 账号 → Webhooks。
3. 给一部 **已在 Lingarr 列表里** 的电影点 5 星 → Translations 出现任务；完成后旁边有 `{stem}.{lang}.srt`。
4. 再点一次 5 星 → 不新建（已 Completed）。
5. 给一集已同步剧集点 5 星 → 同样入队。
6. 点 4 星 / 清星 → 不入队。
7. Plex 有、Lingarr 列表没有的片点 5 星 → 日志 skip，无新 Movie 行，无翻译任务。
8. 仅 MKV 内嵌英文字幕的 Arr 片 → 任务带抽轨进度（云盘可能很久），不要在 webhook 队列里卡死。
9. `scan_embedded_subtitles=false` 时五星仍能译内嵌（与自动扫描解耦）。
10. Disconnect 后五星不再能补路径（日志提示未授权）；webhook secret 仍可校验入站请求。
11. 用 Plex Home **托管账户**（某台电视登录的那个）给已同步电影点 5 星 → 同样入队。默认不要拦。

---

## 12. 使用前检查（写进设置说明）

1. 先 **Sign in with Plex**，不要去 Plex 里找 Token  
2. 片必须先被 Radarr/Sonarr 同步进 Lingarr  
3. Mapping：Plex `Part.file` 能转到 Lingarr 的 `Path` + `FileName`  
4. Lingarr 容器挂载了那些视频目录  
5. `source_languages` / `target_languages` 已配  
6. Webhook URL 是 **Lingarr** 地址，且 PMS 进程能访问  
7. 不要把主 API key 或 plex.tv token 填进 Plex Webhooks  

---

## 13. 后续与未做项

孤儿片路径翻译与整季五星已另案实施，见 `docs/plex-orphan-five-star-path-translation-plan.md`（替换本节原先的「孤儿片」条目：按路径译、不写入 Movies/Shows）。

本文仍不实施：

- 无 Plex Pass：轮询 `userRating >= 10`
- 译完 `GET /library/sections/{id}/refresh?path=`
- 标题+年份唯一命中兜底
- Mapping 链到设置侧栏（现在是隐藏路由）

---

## 14. 关键现成代码（插入点）

| 角色 | 路径 |
|---|---|
| Arr webhook HTTP | `Lingarr.Server/Controllers/WebhookController.cs` |
| Arr webhook job | `Lingarr.Server/Jobs/WebhookJob.cs` |
| 鉴权 | `Lingarr.Server/Attributes/LingarrAuthorizeAttribute.cs`（Plex **webhook** 端点不要走这个） |
| 入队 | `Lingarr.Server/Services/TranslationRequestService.CreateRequest` |
| 自动选源（不要改） | `SubtitleService.SelectSourceSubtitle`、`MediaSubtitleProcessor` |
| 内嵌合并 | `Lingarr.Server/Services/Media/EmbeddedSubtitleMerger.cs` |
| 占位 | `Lingarr.Server/Services/Media/EmbeddedSubtitlePlaceholder.cs` |
| 路径映射 | `Lingarr.Server/Services/PathConversionService.cs` |
| Arr id 解析 | `MediaService.GetMovieIdOrSyncFromRadarrMovieId` / `GetEpisodeIdOrSyncFromSonarrEpisodeId` |
| 设置页 | `Lingarr.Client/src/pages/settings/IntegrationPage.vue` |
| Mapping UI | `Lingarr.Client/src/pages/settings/MappingPage.vue` |
| 加密设置 | `SettingController` `encrypted`；API key 生成可参考 `AuthController.apikey/generate` |
| 实体 | `Lingarr.Core/Entities/Movie.cs`（`RadarrId` 必填）、`Episode.cs` |
