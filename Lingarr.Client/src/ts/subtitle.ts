export interface ISubtitle {
    path: string
    language: string
    fileName: string
    format: string
    caption: string
    isEmbedded?: boolean
    trackIndex?: number | null
}

export type ProbeEmbeddedReason = 'no_mkv' | 'no_ffprobe' | 'timeout' | 'probe_failed'

export interface IEmbeddedSubtitleTrack {
    index: number
    language: string
    codecName: string
    extension: string
    title: string
    caption: string
    isDefault: boolean
    isForced: boolean
    isHearingImpaired: boolean
    canAutoTranslate: boolean
    sidecarExists: boolean
    extractable: boolean
}

export interface ISkippedEmbeddedTrack {
    index: number
    codecName: string
    reason: string
    language?: string
    caption?: string
    isForced?: boolean
}

export interface IProbeEmbeddedResponse {
    found: boolean
    reason?: ProbeEmbeddedReason | string
    fileName?: string
    tracks?: IEmbeddedSubtitleTrack[]
    skipped?: ISkippedEmbeddedTrack[]
}

export interface IExtractAndTranslateRequest {
    mediaId: number
    mediaType: 'Movie' | 'Episode'
    trackIndex: number
    sourceLanguage: string
    targetLanguage: string
}

export interface IExtractAndTranslateResponse {
    jobId: number
}

export function formatSubtitlePath(path?: string | null): string {
    if (!path) {
        return ''
    }

    const prefix = 'embedded:'
    if (!path.startsWith(prefix)) {
        return path
    }

    const remainder = path.slice(prefix.length)
    const lastColon = remainder.lastIndexOf(':')
    if (lastColon <= 0 || lastColon === remainder.length - 1) {
        return path
    }

    const mkvPath = remainder.slice(0, lastColon)
    const trackIndex = remainder.slice(lastColon + 1)
    const fileName = mkvPath.split(/[/\\]/).pop() || mkvPath
    return `Embedded track #${trackIndex} in ${fileName}`
}

export function apiErrorMessage(error: unknown, fallback: string): string {
    if (!error || typeof error !== 'object') {
        return fallback
    }

    const candidate = error as { data?: { error?: string; Error?: string }; message?: string }
    return candidate.data?.error || candidate.data?.Error || fallback
}
