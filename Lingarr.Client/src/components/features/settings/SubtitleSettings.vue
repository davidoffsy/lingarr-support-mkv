<template>
    <CardComponent title="Subtitle">
        <template #description>
            Configure how subtitles are scanned, cleaned, and timed. Embedded MKV text tracks can
            be extracted as sidecars next to the video for Plex / Emby.
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <SaveNotification ref="saveNotification" />
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Scan embedded MKV subtitles:</span>
                    When enabled, automatic and bulk translation can use MKV text tracks
                    (SRT / ASS / SSA) as a source. Auto jobs always probe the MKV to see if the
                    target language is already present, including image tracks such as PGS.
                    Forced-only tracks do not count. Movie and show lists are not probed.
                    Enabled by default on new installations; existing settings are preserved.
                </div>
                <ToggleButton v-model="scanEmbeddedSubtitles">
                    <span class="text-primary-content text-sm font-medium">
                        {{ scanEmbeddedSubtitles == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Extract embedded source:</span>
                    Write the selected source track as a sidecar next to the video so Plex / Emby
                    can switch languages. Translation of embedded tracks always extracts this file
                    first. Results use
                    <code class="text-sm">{{ '{video}.{language}[.caption].srt|ass' }}</code>
                    in the same folder as the video.
                </div>
                <ToggleButton v-model="extractEmbeddedSource">
                    <span class="text-primary-content text-sm font-medium">
                        {{ extractEmbeddedSource == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <InputComponent
                    v-model="extractTimeoutMinutes"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    label="Embedded extract timeout (minutes):"
                    @update:validation="(val) => (isValid.extractTimeoutMinutes = val)">
                    <p class="text-primary-content/60 text-xs">
                        ffmpeg may read the entire video when extracting a track. Cloud / rclone
                        mounts are often slow. Default is 60 for on-demand extract (the MKV
                        button). Automatic / bulk extract stays at 2 minutes.
                    </p>
                </InputComponent>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        Skip translation if subtitles with caption tags exist:
                    </span>
                    Prefer a regular (non-forced / non-SDH) source track when one exists.
                    A forced-only target track is not treated as the language already being present.
                </div>
                <ToggleButton v-model="ignoreCaptions">
                    <span class="text-primary-content text-sm font-medium">
                        {{ ignoreCaptions == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Fix overlapping subtitles:</span>
                    Automatically resolves overlapping subtitles by trimming the end time of the
                    earlier subtitle. This won't work on subtitles that are out of sync.
                    And in most cases it also caused subtitles to go out of sync.
                </div>
                <ToggleButton v-model="fixOverlappingSubtitles">
                    <span class="text-primary-content text-sm font-medium">
                        {{ fixOverlappingSubtitles == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Strip subtitle formatting:</span>
                    Enable this option to remove all formatting tags (e.g., italics, bold, color,
                    position) from (SRT) subtitles, resulting in plain text subtitles.
                </div>
                <ToggleButton v-model="stripSubtitleFormatting">
                    <span class="text-primary-content text-sm font-medium">
                        {{ stripSubtitleFormatting == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Preserve line breaks:</span>
                    Keep multi-line subtitles split across lines after translation instead of
                    merging them into a single line. AI services translate the whole subtitle in one
                    call; classic services (DeepL, Google, Libre, etc.) translate each line
                    separately.
                </div>
                <ToggleButton v-model="preserveLineBreaks">
                    <span class="text-primary-content text-sm font-medium">
                        {{ preserveLineBreaks == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Strip trailing full-width period:</span>
                    Remove a single ideographic period (。) from the end of each subtitle when
                    writing the file. Periods in the middle, consecutive periods (。。。), and other
                    punctuation are not changed. Off by default.
                </div>
                <ToggleButton v-model="stripTrailingIdeographicFullStop">
                    <span class="text-primary-content text-sm font-medium">
                        {{ stripTrailingIdeographicFullStop == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Add translator info</span>
                    Add translator info at the beginning of subtitles.
                </div>
                <ToggleButton v-model="addTranslatorInfo">
                    <span class="text-primary-content text-sm font-medium">
                        {{ addTranslatorInfo == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Remove language tag</span>
                    Remove language tags (e.g., ".en.") from file names.
                </div>
                <ToggleButton v-model="removeLanguageTag">
                    <span class="text-primary-content text-sm font-medium">
                        {{ removeLanguageTag == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-y-4">
                    <div class="flex flex-col space-x-2">
                        <span class="font-semibold">Use subtitle tagging</span>
                        Appends a custom segment to the subtitle filename however. This is not a
                        recognised subtitle standard and may cause issues with other apps.
                    </div>
                    <ToggleButton v-model="useSubtitleTagging">
                        <span class="text-primary-content text-sm font-medium">
                            {{ useSubtitleTagging == 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>
                    <InputComponent
                        v-if="useSubtitleTagging == 'true'"
                        v-model="subtitleTag"
                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                        label="Subtitle tag"
                        @update:validation="(val) => (isValid.subtitleTag = val)" />
                </div>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Also export bilingual ASS</span>
                    When enabled, translation still writes the normal target subtitle, plus a
                    <code class="text-sm">.bilingual.ass</code>
                    file with larger target text over smaller source text. Off by default.
                </div>
                <ToggleButton v-model="exportBilingualAss">
                    <span class="text-primary-content text-sm font-medium">
                        {{ exportBilingualAss == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { ref, computed, reactive } from 'vue'
import { INPUT_VALIDATION_TYPE, SETTINGS } from '@/ts'
import { useSettingStore } from '@/store/setting'

import CardComponent from '@/components/common/CardComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import InputComponent from '@/components/common/InputComponent.vue'

const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const settingsStore = useSettingStore()
const isValid = reactive({
    subtitleTag: true,
    extractTimeoutMinutes: true
})

const scanEmbeddedSubtitles = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.SCAN_EMBEDDED_SUBTITLES) as string) ?? 'true',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SCAN_EMBEDDED_SUBTITLES, newValue, true)
        saveNotification.value?.show()
    }
})

const extractEmbeddedSource = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.EXTRACT_EMBEDDED_SOURCE) as string) ?? 'true',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.EXTRACT_EMBEDDED_SOURCE, newValue, true)
        saveNotification.value?.show()
    }
})

const extractTimeoutMinutes = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.EXTRACT_TIMEOUT_MINUTES) as string) ?? '60',
    set: (newValue: string): void => {
        const parsed = Number.parseInt(newValue, 10)
        const clamped =
            Number.isFinite(parsed) && parsed > 0 ? String(Math.min(Math.max(parsed, 1), 1440)) : newValue
        settingsStore.updateSetting(
            SETTINGS.EXTRACT_TIMEOUT_MINUTES,
            clamped,
            isValid.extractTimeoutMinutes
        )
        if (isValid.extractTimeoutMinutes) {
            saveNotification.value?.show()
        }
    }
})

const ignoreCaptions = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.IGNORE_CAPTIONS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.IGNORE_CAPTIONS, newValue, true)
        saveNotification.value?.show()
    }
})

const fixOverlappingSubtitles = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.FIX_OVERLAPPING_SUBTITLES) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.FIX_OVERLAPPING_SUBTITLES, newValue, true)
        saveNotification.value?.show()
    }
})

const stripSubtitleFormatting = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.STRIP_SUBTITLE_FORMATTING) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.STRIP_SUBTITLE_FORMATTING, newValue, true)
        saveNotification.value?.show()
    }
})

const preserveLineBreaks = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.PRESERVE_LINE_BREAKS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.PRESERVE_LINE_BREAKS, newValue, true)
        saveNotification.value?.show()
    }
})

const stripTrailingIdeographicFullStop = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.STRIP_TRAILING_IDEOGRAPHIC_FULL_STOP) as string) ??
        'false',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.STRIP_TRAILING_IDEOGRAPHIC_FULL_STOP, newValue, true)
        saveNotification.value?.show()
    }
})

const addTranslatorInfo = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.ADD_TRANSLATOR_INFO) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.ADD_TRANSLATOR_INFO, newValue, true)
        saveNotification.value?.show()
    }
})

const removeLanguageTag = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.REMOVE_LANGUAGE_TAG) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.REMOVE_LANGUAGE_TAG, newValue, true)
        saveNotification.value?.show()
    }
})

const useSubtitleTagging = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.USE_SUBTITLE_TAGGING) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.USE_SUBTITLE_TAGGING, newValue, true)
        saveNotification.value?.show()
    }
})

const subtitleTag = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SUBTITLE_TAG) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SUBTITLE_TAG, newValue, isValid.subtitleTag)
        saveNotification.value?.show()
    }
})

const exportBilingualAss = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.EXPORT_BILINGUAL_ASS) as string) ?? 'false',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.EXPORT_BILINGUAL_ASS, newValue, true)
        saveNotification.value?.show()
    }
})
</script>
