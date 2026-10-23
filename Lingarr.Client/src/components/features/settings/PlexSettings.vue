<template>
    <CardComponent title="Plex">
        <template #description>
            Sign in with Plex to translate movies, episodes, and seasons when they are rated five stars.
        </template>
        <template #content>
            <SaveNotification ref="saveNotification" />
            <div class="flex flex-col space-y-4">
                <div v-if="!isLinked" class="flex flex-col space-y-2">
                    <ButtonComponent
                        variant="primary"
                        :loading="signingIn"
                        :disabled="signingIn"
                        @click="signInWithPlex">
                        {{ signingIn ? 'Waiting for Plex…' : 'Sign in with Plex' }}
                    </ButtonComponent>
                    <p v-if="signInError" class="text-sm text-red-500">
                        {{ signInError }}
                        <a
                            v-if="pendingAuthUrl"
                            :href="pendingAuthUrl"
                            :target="PLEX_AUTH_WINDOW_NAME"
                            class="underline">
                            Open Plex sign-in
                        </a>
                    </p>
                    <p v-else-if="signingIn && pendingAuthUrl" class="text-sm text-secondary-content/80">
                        If the Plex window is blank,
                        <a :href="pendingAuthUrl" :target="PLEX_AUTH_WINDOW_NAME" class="underline">
                            open sign-in here
                        </a>.
                    </p>
                </div>
                <div v-else class="flex flex-col space-y-2">
                    <span class="font-semibold">Signed in as {{ accountTitle || 'Plex account' }}</span>
                    <ButtonComponent variant="secondary" :loading="disconnecting" @click="disconnect">
                        Disconnect
                    </ButtonComponent>
                </div>

                <div v-if="isLinked" class="flex flex-col space-y-2">
                    <SelectComponent
                        label="Server"
                        placeholder="Select a Plex server"
                        no-options="No owned servers found."
                        :options="serverOptions"
                        :selected="machineId"
                        @update:selected="onServerSelected"
                        @fetch-options="loadServers"
                        load-on-open />
                    <InputComponent
                        v-model="plexUrl"
                        :validation-type="INPUT_VALIDATION_TYPE.URL"
                        label="Server address"
                        error-message="Please enter a valid URL (e.g., http://plex:32400)"
                        @update:validation="(val) => (isUrlValid = val)" />
                    <div class="flex items-center space-x-2">
                        <ButtonComponent
                            variant="secondary"
                            :loading="testing"
                            :disabled="testing"
                            @click="testConnection">
                            Test connection
                        </ButtonComponent>
                    </div>
                    <StatusMessage
                        v-if="testMessage"
                        :message="testMessage"
                        :type="testSuccess ? 'success' : 'error'"
                        show-icon />
                </div>

                <div class="flex flex-col space-y-2">
                    <span class="font-semibold">Webhook URL</span>
                    <code class="block break-all rounded bg-black/20 p-3 font-mono text-sm">
                        {{ webhookUrl || 'Generating…' }}
                    </code>
                    <div class="flex flex-wrap gap-2">
                        <ButtonComponent variant="secondary" :disabled="!webhookUrl" @click="copyWebhookUrl">
                            {{ copied ? 'Copied' : 'Copy' }}
                        </ButtonComponent>
                        <ButtonComponent variant="ghost" :loading="regenerating" @click="regenerateSecret">
                            Regenerate secret
                        </ButtonComponent>
                        <ButtonComponent variant="ghost" :loading="checkingWebhook" @click="loadWebhookStatus">
                            Check last webhook
                        </ButtonComponent>
                    </div>
                    <StatusMessage
                        v-if="webhookChecked"
                        :message="webhookStatusMessage"
                        :type="webhookStatusType"
                        show-icon />
                </div>

                <InputComponent
                    v-model="minRating"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :type="INPUT_TYPE.NUMBER"
                    label="Minimum rating"
                    error-message="Use Plex's 10-point scale (5 stars = 10)"
                    @update:validation="(val) => (isMinRatingValid = val)" />

                <SelectComponent
                    label="Who can trigger"
                    :options="raterOptions"
                    :selected="allowedRaters"
                    @update:selected="onRatersSelected" />

                <p class="text-secondary-content/80 text-sm">
                    Paste the webhook URL into Plex Web → Account → Webhooks. Plex Media Server must be able to
                    reach Lingarr. If Plex and Radarr/Sonarr paths differ, add Movie / Show mappings at
                    <router-link to="/settings/mapping" class="underline">/settings/mapping</router-link>.
                    Keep <strong>Remove language tag</strong> off (Settings → Subtitle) so Plex can pick
                    language-coded sidecars such as <code>{stem}.{lang}.srt</code>.
                    Titles not in Radarr/Sonarr still translate when Lingarr can read the mapped file; they
                    are not added to Movies or Shows. Rate a season on the season page in Plex Web — the
                    series poster is ignored.
                </p>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useSettingStore } from '@/store/setting'
import services from '@/services'
import {
    ENCRYPTED_SETTINGS,
    INPUT_TYPE,
    INPUT_VALIDATION_TYPE,
    IPlexServer,
    IPlexWebhookStatus,
    ISelectOption,
    SETTINGS
} from '@/ts'
import { resolveUrl } from '@/utils/baseUrl'
import {
    PLEX_AUTH_WINDOW_NAME,
    closePlexAuthWindow,
    navigatePlexAuthWindow,
    openPlexAuthWindow,
    showPlexAuthWaitingPage
} from '@/utils/plexAuthPopup'
import CardComponent from '@/components/common/CardComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import SelectComponent from '@/components/common/SelectComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import StatusMessage from '@/components/common/StatusMessage.vue'

const POLL_INTERVAL_MS = 1500
const POLL_TIMEOUT_MS = 15 * 60 * 1000

const settingsStore = useSettingStore()
const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const signingIn = ref(false)
const disconnecting = ref(false)
const testing = ref(false)
const regenerating = ref(false)
const checkingWebhook = ref(false)
const webhookChecked = ref(false)
const webhookStatus = ref<IPlexWebhookStatus | null>(null)
const copied = ref(false)
const signInError = ref('')
const pendingAuthUrl = ref('')
const testMessage = ref('')
const testSuccess = ref(false)
const isUrlValid = ref(false)
const isMinRatingValid = ref(true)
const servers = ref<IPlexServer[]>([])
let pollTimer: ReturnType<typeof setInterval> | null = null
let pollTimeout: ReturnType<typeof setTimeout> | null = null
let authPopup: Window | null = null

const raterOptions: ISelectOption[] = [
    { value: 'home', label: 'Home users (owner + managed)' },
    { value: 'anyone', label: 'Anyone' }
]

const accountTitle = computed(
    () => (settingsStore.getSetting(SETTINGS.PLEX_ACCOUNT_TITLE) as string) ?? ''
)
const isLinked = computed(() => {
    if (accountTitle.value.trim().length > 0) {
        return true
    }
    const completed = (settingsStore.getSetting(SETTINGS.PLEX_SETTINGS_COMPLETED) as string) ?? ''
    return completed.toLowerCase() === 'true'
})

const machineId = computed({
    get: (): string => (settingsStore.getSetting(SETTINGS.PLEX_MACHINE_ID) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.PLEX_MACHINE_ID, value, true)
        saveNotification.value?.show()
    }
})

const plexUrl = computed({
    get: (): string => (settingsStore.getSetting(SETTINGS.PLEX_URL) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.PLEX_URL, value, isUrlValid.value)
        if (isUrlValid.value) {
            if (isLinked.value) {
                settingsStore.updateSetting(SETTINGS.PLEX_SETTINGS_COMPLETED, 'true', true)
            }
            saveNotification.value?.show()
        }
    }
})

const minRating = computed({
    get: (): string => (settingsStore.getSetting(SETTINGS.PLEX_MIN_RATING) as string) || '10',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.PLEX_MIN_RATING, value, isMinRatingValid.value)
        if (isMinRatingValid.value) {
            saveNotification.value?.show()
        }
    }
})

const allowedRaters = computed(
    () => (settingsStore.getSetting(SETTINGS.PLEX_ALLOWED_RATERS) as string) || 'home'
)

const webhookSecret = computed(
    () => settingsStore.getEncryptedSetting(ENCRYPTED_SETTINGS.PLEX_WEBHOOK_SECRET) ?? ''
)

const webhookUrl = computed(() => {
    if (!webhookSecret.value) {
        return ''
    }
    return `${resolveUrl('/api/webhook/plex')}?token=${webhookSecret.value}`
})

const webhookStatusType = computed(() => {
    const outcome = webhookStatus.value?.outcome
    if (outcome === 'queued') {
        return 'success'
    }
    if (outcome === 'ignored' || outcome === 'rejected' || outcome === 'invalid') {
        return 'error'
    }
    return 'info'
})

const webhookStatusMessage = computed(() => {
    const status = webhookStatus.value
    if (!status) {
        return 'No webhook received yet. Opening this URL or curling GET / only proves the page loads — Plex Media Server must POST multipart/form-data. After a saved 5-star rating, check again. If it stays empty, PMS is not sending (Plex Pass, webhook URL, or nginx rejecting the POST body).'
    }

    const when = new Date(status.receivedAt).toLocaleString()
    const title = status.title ? ` “${status.title}”` : ''
    const event = status.event ?? 'unknown event'
    const extra = status.reason ? `: ${status.reason}` : ''
    return `Last webhook ${when}: ${status.outcome} ${event}${title}${extra}`
})

const serverOptions = computed<ISelectOption[]>(() =>
    servers.value.map((server) => ({
        value: server.machineId,
        label: server.name
    }))
)

const stopPolling = () => {
    if (pollTimer) {
        clearInterval(pollTimer)
        pollTimer = null
    }
    if (pollTimeout) {
        clearTimeout(pollTimeout)
        pollTimeout = null
    }
    signingIn.value = false
    closePlexAuthWindow(authPopup)
    authPopup = null
}

const storeSecret = (secret: string) => {
    settingsStore.encrypted_settings[ENCRYPTED_SETTINGS.PLEX_WEBHOOK_SECRET] = secret
}

const ensureWebhookSecret = async () => {
    if (webhookSecret.value) {
        return
    }
    const response = await services.plex.getWebhookSecret()
    storeSecret(response.secret)
}

const loadServers = async () => {
    if (!isLinked.value) {
        return
    }
    try {
        servers.value = await services.plex.getServers()
    } catch {
        servers.value = []
    }
}

const signInWithPlex = async () => {
    signInError.value = ''
    pendingAuthUrl.value = ''
    stopPolling()
    signingIn.value = true
    authPopup = openPlexAuthWindow()
    if (authPopup) {
        showPlexAuthWaitingPage(authPopup)
    } else {
        signInError.value = 'Pop-up blocked. Allow pop-ups for this site and try again.'
    }
    try {
        const pin = await services.plex.createPin()
        pendingAuthUrl.value = pin.authUrl
        if (!navigatePlexAuthWindow(authPopup, pin.authUrl) && !signInError.value) {
            signInError.value = 'Pop-up blocked. Allow pop-ups for this site and try again.'
        }

        pollTimer = setInterval(async () => {
            try {
                const status = await services.plex.pollPin(pin.pinId)
                if (status.linked) {
                    pendingAuthUrl.value = ''
                    stopPolling()
                    await settingsStore.updateSetting(
                        SETTINGS.PLEX_ACCOUNT_TITLE,
                        status.accountTitle ?? '',
                        true
                    )
                    await loadServers()
                    saveNotification.value?.show()
                }
            } catch {
                // Keep polling until timeout; transient plex.tv errors should not abort Sign in.
            }
        }, POLL_INTERVAL_MS)

        pollTimeout = setTimeout(() => {
            pendingAuthUrl.value = ''
            stopPolling()
            signInError.value = 'Sign in timed out. Please try again.'
        }, POLL_TIMEOUT_MS)
    } catch {
        closePlexAuthWindow(authPopup)
        authPopup = null
        signingIn.value = false
        signInError.value = 'Could not start Sign in with Plex.'
    }
}

const disconnect = async () => {
    disconnecting.value = true
    try {
        await services.plex.disconnect()
        settingsStore.storeSetting(SETTINGS.PLEX_ACCOUNT_TITLE, '')
        settingsStore.storeSetting(SETTINGS.PLEX_MACHINE_ID, '')
        settingsStore.storeSetting(SETTINGS.PLEX_SETTINGS_COMPLETED, 'false')
        servers.value = []
        testMessage.value = ''
    } finally {
        disconnecting.value = false
    }
}

const onServerSelected = async (value: string) => {
    const server = servers.value.find((item) => item.machineId === value)
    machineId.value = value
    if (server?.preferredUri) {
        await settingsStore.updateSetting(SETTINGS.PLEX_URL, server.preferredUri, true)
        await settingsStore.updateSetting(SETTINGS.PLEX_SETTINGS_COMPLETED, 'true', true)
        saveNotification.value?.show()
    }
}

const onRatersSelected = (value: string) => {
    settingsStore.updateSetting(SETTINGS.PLEX_ALLOWED_RATERS, value, true)
    saveNotification.value?.show()
}

const testConnection = async () => {
    testing.value = true
    testMessage.value = ''
    try {
        const result = await services.plex.testConnection(plexUrl.value || undefined)
        testSuccess.value = result.success
        testMessage.value = result.message ?? (result.success ? 'Connected' : 'Connection failed')
        if (result.success) {
            await settingsStore.updateSetting(SETTINGS.PLEX_SETTINGS_COMPLETED, 'true', true)
        }
    } catch {
        testSuccess.value = false
        testMessage.value = 'Connection failed'
    } finally {
        testing.value = false
    }
}

const copyWebhookUrl = async () => {
    if (!webhookUrl.value) {
        return
    }
    await navigator.clipboard.writeText(webhookUrl.value)
    copied.value = true
    window.setTimeout(() => {
        copied.value = false
    }, 2000)
}

const loadWebhookStatus = async () => {
    checkingWebhook.value = true
    try {
        webhookStatus.value = await services.plex.getWebhookStatus()
    } catch {
        webhookStatus.value = null
    } finally {
        webhookChecked.value = true
        checkingWebhook.value = false
    }
}

const regenerateSecret = async () => {
    if (!window.confirm('Regenerating this secret invalidates the Plex webhook URL. Continue?')) {
        return
    }

    regenerating.value = true
    try {
        const response = await services.plex.regenerateWebhookSecret()
        storeSecret(response.secret)
        saveNotification.value?.show()
    } finally {
        regenerating.value = false
    }
}

onMounted(async () => {
    await ensureWebhookSecret()
    await loadServers()
})

onUnmounted(() => {
    stopPolling()
})
</script>
