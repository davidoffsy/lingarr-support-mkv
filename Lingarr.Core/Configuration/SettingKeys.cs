namespace Lingarr.Core.Configuration;

public static class SettingKeys
{
    public static class Integration
    {
        public const string RadarrUrl = "radarr_url";
        public const string RadarrApiKey = "radarr_api_key";
        public const string RadarrDefaultInclude = "radarr_default_include";
        public const string SonarrUrl = "sonarr_url";
        public const string SonarrApiKey = "sonarr_api_key";
        public const string SonarrDefaultInclude = "sonarr_default_include";
        public const string ArrWebhookSecret = "arr_webhook_secret";
        public const string RadarrWebhookAutoTranslate = "radarr_webhook_auto_translate";
        public const string SonarrWebhookAutoTranslate = "sonarr_webhook_auto_translate";
        /// <summary>
        /// Legacy combined toggle. Kept so unmigrated databases still honor the old value.
        /// </summary>
        public const string ArrWebhookAutoTranslate = "arr_webhook_auto_translate";
        public const string RadarrSettingsCompleted = "radarr_settings_completed";
        public const string SonarrSettingsCompleted = "sonarr_settings_completed";
        public const string PlexUrl = "plex_url";
        public const string PlexToken = "plex_token";
        public const string PlexClientIdentifier = "plex_client_identifier";
        public const string PlexMachineId = "plex_machine_id";
        public const string PlexAccountTitle = "plex_account_title";
        public const string PlexWebhookSecret = "plex_webhook_secret";
        public const string PlexMinRating = "plex_min_rating";
        public const string PlexAllowedRaters = "plex_allowed_raters";
        public const string PlexSettingsCompleted = "plex_settings_completed";
    }

    public static class Translation
    {
        public const string ServiceType = "service_type";
        public const string DefaultServiceType = "libretranslate";

        public static class OpenAi
        {
            public const string Model = "openai_model";
            public const string ApiKey = "openai_api_key";
            public const string RequestTemplate = "openai_request_template";
        }

        public static class Anthropic
        {
            public const string Model = "anthropic_model";
            public const string ApiKey = "anthropic_api_key";
            public const string Version = "anthropic_version";
            public const string RequestTemplate = "anthropic_request_template";
        }

        public static class LocalAi
        {
            public const string Model = "local_ai_model";
            public const string Endpoint = "local_ai_endpoint";
            public const string ApiKey = "local_ai_api_key";
            public const string Protocol = "local_ai_protocol";
            public const string ChatRequestTemplate = "local_ai_chat_request_template";
            public const string GenerateRequestTemplate = "local_ai_generate_request_template";

            public static class Protocols
            {
                public const string Auto = "auto";
                public const string OpenAiChat = "openai_chat";
                public const string OllamaGenerate = "ollama_generate";
            }
        }

        /// <summary>
        /// Independent custom provider (OpenAI Chat, OpenAI Responses, or Anthropic Messages).
        /// Shares no keys with <see cref="LocalAi"/> or official Anthropic, so those providers
        /// can be configured and used in the same translation chain.
        /// </summary>
        public static class Custom
        {
            public const string Model = "custom_model";
            public const string Endpoint = "custom_endpoint";
            public const string ApiKey = "custom_api_key";
            public const string Protocol = "custom_protocol";
            public const string ChatRequestTemplate = "custom_chat_request_template";
            public const string ResponsesRequestTemplate = "custom_responses_request_template";
            public const string MessagesRequestTemplate = "custom_messages_request_template";
            public const string AnthropicVersion = "custom_anthropic_version";

            /// <summary>
            /// Protocols Custom can speak. Every protocol is non-streaming and keeps its own request
            /// template key, so switching protocols never overwrites the other protocol's edits.
            /// </summary>
            public static class Protocols
            {
                public const string OpenAiChat = "openai_chat";
                public const string OpenAiResponses = "openai_responses";
                public const string AnthropicMessages = "anthropic_messages";
            }
        }

        public static class DeepL
        {
            public const string DeeplApiKey = "deepl_api_key";
        }

        public static class Gemini
        {
            public const string Model = "gemini_model";
            public const string ApiKey = "gemini_api_key";
            public const string RequestTemplate = "gemini_request_template";
        }

        public static class DeepSeek
        {
            public const string Model = "deepseek_model";
            public const string ApiKey = "deepseek_api_key";
            public const string RequestTemplate = "deepseek_request_template";
        }

        public static class Mistral
        {
            public const string Model = "mistral_model";
            public const string ApiKey = "mistral_api_key";
            public const string RequestTemplate = "mistral_request_template";
        }

        public static class XAi
        {
            public const string Model = "xai_model";
            public const string ApiKey = "xai_api_key";
            public const string RequestTemplate = "xai_request_template";
        }

        public static class LibreTranslate
        {
            public const string Url = "libretranslate_url";
            public const string ApiKey = "libretranslate_api_key";
        }

        public const string SourceLanguages = "source_languages";
        public const string TargetLanguages = "target_languages";
        public const string AiPrompt = "ai_prompt";
        public const string AiUserPrompt = "ai_user_prompt";
        public const string AiMaxTokens = "ai_max_tokens";
        public const string AiThinking = "ai_thinking";
        public const string ProofreadPrompt = "proofread_prompt";
        public const string ProofreadUserPrompt = "proofread_user_prompt";
        public const string AiContextBefore = "ai_context_before";
        public const string AiContextAfter = "ai_context_after";
        public const string AiContextUseTranslated = "ai_context_use_translated";
        public const string FixOverlappingSubtitles = "fix_overlapping_subtitles";
        public const string StripSubtitleFormatting = "strip_subtitle_formatting";
        public const string StripTrailingIdeographicFullStop = "strip_trailing_ideographic_full_stop";
        public const string ReplaceInlineIdeographicFullStop = "replace_inline_ideographic_full_stop";
        public const string PreserveLineBreaks = "preserve_line_breaks";
        public const string AddTranslatorInfo = "add_translator_info";
        public const string ExportBilingualAss = "export_bilingual_ass";
        public const string UseBatchTranslation = "use_batch_translation";
        public const string MaxBatchSize = "max_batch_size";
        public const string UseSubtitleTagging = "use_subtitle_tagging";
        public const string RemoveLanguageTag = "remove_language_tag";
        public const string SubtitleTag = "subtitle_tag";
        public const string IgnoreCaptions = "ignore_captions";
        public const string ScanEmbeddedSubtitles = "scan_embedded_subtitles";
        public const string ExtractEmbeddedSource = "extract_embedded_source";
        public const string ExtractTimeoutMinutes = "extract_timeout_minutes";
        public const string RequestTimeout = "request_timeout";
        public const string MaxRetries = "max_retries";
        public const string RetryDelay = "retry_delay";
        public const string RetryDelayMultiplier = "retry_delay_multiplier";
        public const string NavigateToDetailsOnRequest = "navigate_to_details_on_request";
        public const string LanguageCodeFormat = "language_code_format";
    }

    public static class Automation
    {
        public const string AutomationEnabled = "automation_enabled";
        public const string TranslationSchedule = "translation_schedule";
        public const string MaxTranslationsPerRun = "max_translations_per_run";
        public const string TranslationCycle = "translation_cycle";
        public const string MovieSchedule = "movie_schedule";
        public const string ShowSchedule = "show_schedule";
        public const string MovieAgeThreshold = "movie_age_threshold";
        public const string ShowAgeThreshold = "show_age_threshold";
    }

    public static class SubtitleValidation
    {
        public const string MaxFileSizeBytes = "subtitle_validation_maxfilesizebytes";
        public const string MaxSubtitleLength = "subtitle_validation_maxsubtitlelength";
        public const string MinSubtitleLength = "subtitle_validation_minsubtitlelength";
        public const string MinDurationMs = "subtitle_validation_mindurationms";
        public const string MaxDurationSecs = "subtitle_validation_maxdurationsecs";
        public const string ValidateSubtitles = "subtitle_validation_enabled";
    }

    public static class Authentication
    {
        public const string AuthEnabled = "auth_enabled";
        public const string ApiKey = "api_key";
        public const string OnboardingCompleted = "onboarding_completed";
    }

    public static class Telemetry
    {
        public const string TelemetryEnabled = "telemetry_enabled";
        public const string LastSubmission = "telemetry_last_submission";
        public const string InstallationId = "telemetry_installation_id";
    }
}
