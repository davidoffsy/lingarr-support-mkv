import { ILanguage } from '@/ts/language'
import { ILocale, ITheme } from '@/ts/store'

export const LOCAL_AI_PROTOCOLS = {
    AUTO: 'auto',
    OPENAI_CHAT: 'openai_chat',
    OLLAMA_GENERATE: 'ollama_generate'
} as const

export const CUSTOM_PROTOCOLS = {
    OPENAI_CHAT: 'openai_chat',
    OPENAI_RESPONSES: 'openai_responses',
    ANTHROPIC_MESSAGES: 'anthropic_messages'
} as const

export const AI_THINKING_MODES = {
    AUTO: 'auto',
    OFF: 'off',
    ON: 'on'
} as const

export const SETTINGS = {
    RADARR_URL: 'radarr_url',
    SONARR_URL: 'sonarr_url',
    SOURCE_LANGUAGES: 'source_languages',
    TARGET_LANGUAGES: 'target_languages',
    SONARR_SETTINGS_COMPLETED: 'sonarr_settings_completed',
    RADARR_SETTINGS_COMPLETED: 'radarr_settings_completed',
    SERVICE_TYPE: 'service_type',
    LIBRETRANSLATE_URL: 'libretranslate_url',
    SHOW_SCHEDULE: 'show_schedule',
    MOVIE_SCHEDULE: 'movie_schedule',
    MAX_TRANSLATIONS_PER_RUN: 'max_translations_per_run',
    AUTOMATION_ENABLED: 'automation_enabled',
    TRANSLATION_SCHEDULE: 'translation_schedule',
    OPENAI_MODEL: 'openai_model',
    ANTHROPIC_MODEL: 'anthropic_model',
    ANTHROPIC_VERSION: 'anthropic_version',
    LOCAL_AI_ENDPOINT: 'local_ai_endpoint',
    LOCAL_AI_MODEL: 'local_ai_model',
    LOCAL_AI_PROTOCOL: 'local_ai_protocol',
    CUSTOM_ENDPOINT: 'custom_endpoint',
    CUSTOM_MODEL: 'custom_model',
    CUSTOM_PROTOCOL: 'custom_protocol',
    CUSTOM_ANTHROPIC_VERSION: 'custom_anthropic_version',
    GEMINI_MODEL: 'gemini_model',
    DEEPSEEK_MODEL: 'deepseek_model',
    MISTRAL_MODEL: 'mistral_model',
    XAI_MODEL: 'xai_model',
    AI_PROMPT: 'ai_prompt',
    AI_USER_PROMPT: 'ai_user_prompt',
    AI_MAX_TOKENS: 'ai_max_tokens',
    AI_THINKING: 'ai_thinking',
    PROOFREAD_PROMPT: 'proofread_prompt',
    PROOFREAD_USER_PROMPT: 'proofread_user_prompt',
    THEME: 'theme',
    LOCALE: 'locale',
    MOVIE_AGE_THRESHOLD: 'movie_age_threshold',
    SHOW_AGE_THRESHOLD: 'show_age_threshold',
    FIX_OVERLAPPING_SUBTITLES: 'fix_overlapping_subtitles',
    STRIP_SUBTITLE_FORMATTING: 'strip_subtitle_formatting',
    STRIP_TRAILING_IDEOGRAPHIC_FULL_STOP: 'strip_trailing_ideographic_full_stop',
    REPLACE_INLINE_IDEOGRAPHIC_FULL_STOP: 'replace_inline_ideographic_full_stop',
    PRESERVE_LINE_BREAKS: 'preserve_line_breaks',
    ADD_TRANSLATOR_INFO: 'add_translator_info',
    EXPORT_BILINGUAL_ASS: 'export_bilingual_ass',
    SUBTITLE_VALIDATION_ENABLED: 'subtitle_validation_enabled',
    SUBTITLE_VALIDATION_MAXDURATIONSECS: 'subtitle_validation_maxdurationsecs',
    SUBTITLE_VALIDATION_MINDURATIONMS: 'subtitle_validation_mindurationms',
    SUBTITLE_VALIDATION_MINSUBTITLELENGTH: 'subtitle_validation_minsubtitlelength',
    SUBTITLE_VALIDATION_MAXSUBTITLELENGTH: 'subtitle_validation_maxsubtitlelength',
    SUBTITLE_VALIDATION_MAXFILESIZEBYTES: 'subtitle_validation_maxfilesizebytes',
    AI_CONTEXT_BEFORE: 'ai_context_before',
    AI_CONTEXT_AFTER: 'ai_context_after',
    AI_CONTEXT_USE_TRANSLATED: 'ai_context_use_translated',
    USE_BATCH_TRANSLATION: 'use_batch_translation',
    MAX_BATCH_SIZE: 'max_batch_size',
    USE_SUBTITLE_TAGGING: 'use_subtitle_tagging',
    REMOVE_LANGUAGE_TAG: 'remove_language_tag',
    SUBTITLE_TAG: 'subtitle_tag',
    IGNORE_CAPTIONS: 'ignore_captions',
    SCAN_EMBEDDED_SUBTITLES: 'scan_embedded_subtitles',
    EXTRACT_EMBEDDED_SOURCE: 'extract_embedded_source',
    EXTRACT_TIMEOUT_MINUTES: 'extract_timeout_minutes',
    REQUEST_TIMEOUT: 'request_timeout',
    MAX_RETRIES: 'max_retries',
    RETRY_DELAY: 'retry_delay',
    RETRY_DELAY_MULTIPLIER: 'retry_delay_multiplier',
    AUTH_ENABLED: 'auth_enabled',
    ONBOARDING_COMPLETED: 'onboarding_completed',
    TELEMETRY_ENABLED: 'telemetry_enabled',
    NAVIGATE_TO_DETAILS_ON_REQUEST: 'navigate_to_details_on_request',
    OPENAI_REQUEST_TEMPLATE: 'openai_request_template',
    ANTHROPIC_REQUEST_TEMPLATE: 'anthropic_request_template',
    LOCAL_AI_CHAT_REQUEST_TEMPLATE: 'local_ai_chat_request_template',
    LOCAL_AI_GENERATE_REQUEST_TEMPLATE: 'local_ai_generate_request_template',
    CUSTOM_CHAT_REQUEST_TEMPLATE: 'custom_chat_request_template',
    CUSTOM_RESPONSES_REQUEST_TEMPLATE: 'custom_responses_request_template',
    CUSTOM_MESSAGES_REQUEST_TEMPLATE: 'custom_messages_request_template',
    DEEPSEEK_REQUEST_TEMPLATE: 'deepseek_request_template',
    GEMINI_REQUEST_TEMPLATE: 'gemini_request_template',
    MISTRAL_REQUEST_TEMPLATE: 'mistral_request_template',
    XAI_REQUEST_TEMPLATE: 'xai_request_template',
    LANGUAGE_CODE_FORMAT: 'language_code_format',
    RADARR_DEFAULT_INCLUDE: 'radarr_default_include',
    SONARR_DEFAULT_INCLUDE: 'sonarr_default_include',
    RADARR_WEBHOOK_AUTO_TRANSLATE: 'radarr_webhook_auto_translate',
    SONARR_WEBHOOK_AUTO_TRANSLATE: 'sonarr_webhook_auto_translate',
    PLEX_URL: 'plex_url',
    PLEX_CLIENT_IDENTIFIER: 'plex_client_identifier',
    PLEX_MACHINE_ID: 'plex_machine_id',
    PLEX_ACCOUNT_TITLE: 'plex_account_title',
    PLEX_MIN_RATING: 'plex_min_rating',
    PLEX_ALLOWED_RATERS: 'plex_allowed_raters',
    PLEX_SETTINGS_COMPLETED: 'plex_settings_completed'
} as const

export interface ISettings {
    radarr_url: string
    sonarr_url: string
    service_type: string
    libretranslate_url: string
    show_schedule: string
    movie_schedule: string
    max_translations_per_run: string
    translation_schedule: string
    source_languages: string | ILanguage[]
    target_languages: string | ILanguage[]
    automation_enabled: string
    sonarr_settings_completed: string
    radarr_settings_completed: string
    openai_model: string
    anthropic_model: string
    anthropic_version: string
    local_ai_endpoint: string
    local_ai_model: string
    local_ai_protocol: string
    custom_endpoint: string
    custom_model: string
    custom_protocol: string
    custom_anthropic_version: string
    gemini_model: string
    deepseek_model: string
    mistral_model: string
    xai_model: string
    ai_prompt: string
    ai_user_prompt: string
    ai_max_tokens: string
    ai_thinking: string
    proofread_prompt: string
    proofread_user_prompt: string
    movie_age_threshold: string
    show_age_threshold: string
    fix_overlapping_subtitles: string
    strip_subtitle_formatting: string
    strip_trailing_ideographic_full_stop: string
    replace_inline_ideographic_full_stop: string
    preserve_line_breaks: string
    add_translator_info: string
    export_bilingual_ass: string
    theme: ITheme
    locale: ILocale
    subtitle_validation_enabled: string
    subtitle_validation_maxfilesizebytes: string
    subtitle_validation_minsubtitlelength: string
    subtitle_validation_maxsubtitlelength: string
    subtitle_validation_mindurationms: string
    subtitle_validation_maxdurationsecs: string
    ai_context_before: string
    ai_context_after: string
    ai_context_use_translated: string
    use_batch_translation: string
    max_batch_size: string
    use_subtitle_tagging: string
    remove_language_tag: string
    subtitle_tag: string
    ignore_captions: string
    scan_embedded_subtitles: string
    extract_embedded_source: string
    extract_timeout_minutes: string
    request_timeout: string
    max_retries: string
    retry_delay: string
    retry_delay_multiplier: string
    auth_enabled: string
    onboarding_completed: string
    telemetry_enabled: string
    navigate_to_details_on_request: string
    openai_request_template: string
    anthropic_request_template: string
    local_ai_chat_request_template: string
    local_ai_generate_request_template: string
    custom_chat_request_template: string
    custom_responses_request_template: string
    custom_messages_request_template: string
    deepseek_request_template: string
    gemini_request_template: string
    mistral_request_template: string
    xai_request_template: string
    language_code_format: string
    radarr_default_include: string
    sonarr_default_include: string
    radarr_webhook_auto_translate: string
    sonarr_webhook_auto_translate: string
    plex_url: string
    plex_client_identifier: string
    plex_machine_id: string
    plex_account_title: string
    plex_min_rating: string
    plex_allowed_raters: string
    plex_settings_completed: string
}


export const ENCRYPTED_SETTINGS = {
    API_KEY: 'api_key',
    RADARR_API_KEY: 'radarr_api_key',
    SONARR_API_KEY: 'sonarr_api_key',
    OPENAI_API_KEY: 'openai_api_key',
    ANTHROPIC_API_KEY: 'anthropic_api_key',
    GEMINI_API_KEY: 'gemini_api_key',
    DEEPSEEK_API_KEY: 'deepseek_api_key',
    MISTRAL_API_KEY: 'mistral_api_key',
    XAI_API_KEY: 'xai_api_key',
    DEEPL_API_KEY: 'deepl_api_key',
    LIBRETRANSLATE_API_KEY: 'libretranslate_api_key',
    LOCAL_AI_API_KEY: 'local_ai_api_key',
    CUSTOM_API_KEY: 'custom_api_key',
    PLEX_WEBHOOK_SECRET: 'plex_webhook_secret'
} as const

export interface IEncryptedSettings {
    api_key: string
    radarr_api_key: string
    sonarr_api_key: string
    openai_api_key: string
    anthropic_api_key: string
    gemini_api_key: string
    deepseek_api_key: string
    mistral_api_key: string
    xai_api_key: string
    deepl_api_key: string
    libretranslate_api_key: string
    local_ai_api_key: string
    custom_api_key: string
    plex_webhook_secret: string
}

export const SERVICE_TYPE = {
    LIBRETRANSLATE: 'libretranslate',
    OPENAI: 'openai',
    ANTHROPIC: 'anthropic',
    LOCALAI: 'localai',
    CUSTOM: 'custom',
    DEEPL: 'deepl',
    GEMINI: 'gemini',
    DEEPSEEK: 'deepseek',
    MISTRAL: 'mistral',
    XAI: 'xai',
    GOOGLE: 'google',
    BING: 'bing',
    MICROSOFT: 'microsoft',
    YANDEX: 'yandex'
} as const

export type ServiceType = (typeof SERVICE_TYPE)[keyof typeof SERVICE_TYPE]

export interface IFilterOptions {
    logLevel: string
}

export interface ILogEntry {
    logLevel: string
    message: string
    timestamp: string
    formattedSource: string
    category: string
    stackTrace?: string
}
