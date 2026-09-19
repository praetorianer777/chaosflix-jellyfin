import { APIRequestContext, expect } from "@playwright/test";
import { userId } from "./jellyfin";

/**
 * Device profiles shaped like the clients this plugin has broken on before.
 * Jellyfin decides direct stream vs transcode from these, so asking it with
 * each profile catches a fix for one client breaking another — which is what
 * kept happening between v0.0.26 and v0.0.29.
 *
 * The plugin turns direct play off for every channel item (#55), so it is the
 * TranscodingProfiles — not the DirectPlayProfiles — that decide whether a
 * stream is copied or re-encoded. They therefore have to be the ones the real
 * clients send, or the suite measures a client that does not exist (#79).
 */

type Profile = {
	Name: string;
	DirectPlayProfiles: Array<{
		Container: string;
		Type: "Video";
		VideoCodec: string;
		AudioCodec: string;
	}>;
	TranscodingProfiles: Array<Record<string, unknown>>;
	CodecProfiles: unknown[];
	SubtitleProfiles: unknown[];
	MaxStreamingBitrate: number;
};

const transcodingProfile = (
	container: string,
	videoCodec: string,
	audioCodec: string,
) => ({
	Container: container,
	Type: "Video",
	VideoCodec: videoCodec,
	AudioCodec: audioCodec,
	Context: "Streaming",
	Protocol: "hls",
});

const profile = (
	name: string,
	directPlay: Array<[container: string, video: string, audio: string]>,
	transcoding: Array<[container: string, video: string, audio: string]>,
): Profile => ({
	Name: name,
	DirectPlayProfiles: directPlay.map(([Container, VideoCodec, AudioCodec]) => ({
		Container,
		Type: "Video" as const,
		VideoCodec,
		AudioCodec,
	})),
	TranscodingProfiles: transcoding.map((t) => transcodingProfile(...t)),
	CodecProfiles: [],
	// Every real client lists the text formats it can fetch beside the video;
	// without them the server assumes it has to burn captions into the picture
	// and re-encodes a talk that carries subtitles (#70).
	SubtitleProfiles: [
		{ Format: "vtt", Method: "External" },
		{ Format: "srt", Method: "External" },
		{ Format: "ass", Method: "External" },
	],
	MaxStreamingBitrate: 120_000_000,
});

export const CHROMECAST = profile(
	"Chromecast",
	[["mp4", "h264", "aac"]],
	[["ts", "h264", "aac"]],
);

/**
 * jellyfin-android offers exactly one video transcoding profile, and its video
 * codec is h264 — see DeviceProfileBuilder.kt. Everything it cannot get by
 * direct play is therefore re-encoded to H.264, WebM included.
 */
export const ANDROID_EXOPLAYER = profile(
	"Android ExoPlayer",
	[
		["mp4", "h264", "aac"],
		["webm", "vp9", "opus"],
	],
	[["ts", "h264", "aac"]],
);

/**
 * Chromium as Playwright ships it: VP9/Opus only, no H.264. jellyfin-web asks
 * for fMP4 segments and lists every codec it can decode, so the server copies
 * VP9 and Opus instead of re-encoding them — taken from what the web client
 * sends on 12.1.
 */
export const BROWSER_WITHOUT_H264 = profile(
	"Chromium without H.264",
	[["webm", "vp9", "opus"]],
	[["mp4", "vp9", "opus"]],
);

export const BROWSER_WITH_H264 = profile(
	"Browser with H.264",
	[
		["mp4", "h264", "aac"],
		["webm", "vp9", "opus"],
	],
	[["mp4", "h264,vp9", "aac,opus"]],
);

export type MediaSource = {
	Id: string;
	Name: string;
	Path: string;
	SupportsDirectPlay: boolean;
	Container: string;
	IsRemote: boolean;
	SupportsDirectStream: boolean;
	TranscodingUrl?: string | null;
	TranscodeReasons?: string[];
	DefaultAudioStreamIndex?: number;
	MediaStreams: Array<{ Type: string; Index: number; Codec?: string }>;
};

const transcodingQuery = (source: MediaSource) =>
	new URLSearchParams((source.TranscodingUrl ?? "").split("?")[1] ?? "");

/**
 * Which streams the server will actually re-encode, read from the codecs it
 * puts in the transcoding url rather than from the reason it gives.
 *
 * TranscodeReasons is not comparable across server versions: 10.11.7 derives
 * the reasons from the direct play profile alone, so a source it is about to
 * re-encode still comes back as "DirectPlayError" only, while 12.1 names
 * VideoCodecNotSupported and AudioCodecNotSupported for the same stream and
 * the same ffmpeg command line (#79). The requested codecs are the same on
 * both, so a stream is copied exactly when its own codec is among them.
 */
export function reencodedStreams(source: MediaSource): string[] {
	if (!source.TranscodingUrl) {
		return [];
	}

	const requested = transcodingQuery(source);
	const codecs = (type: "Video" | "Audio") =>
		(requested.get(`${type}Codec`) ?? "").split(",").filter(Boolean);
	// Only the default audio stream is transcoded, and only the first video
	// stream: the second one on a three stream talk is never mapped.
	const played = (type: "Video" | "Audio") =>
		type === "Audio"
			? (source.MediaStreams.find(
					(s) =>
						s.Type === "Audio" && s.Index === source.DefaultAudioStreamIndex,
				) ?? source.MediaStreams.find((s) => s.Type === "Audio"))
			: source.MediaStreams.find((s) => s.Type === "Video");

	return (["Video", "Audio"] as const).flatMap((type) => {
		const stream = played(type);
		const allowed = codecs(type);
		if (
			!stream?.Codec ||
			allowed.length === 0 ||
			allowed.includes(stream.Codec)
		) {
			return [];
		}
		return [`${type} ${stream.Codec}→${allowed[0]}`];
	});
}

/**
 * Why not assert SupportsDirectStream: as soon as a device profile is sent,
 * Jellyfin answers channel items with an HLS url, because the plugin marks the
 * source SupportsDirectPlay=false. What decides whether anything is actually
 * re-encoded is the transcode reason — "DirectPlayError" alone means ffmpeg
 * only remuxes, while a codec or container reason means a real re-encode. The
 * Android suite sees the same thing from the other side (IsVideoDirect=true).
 * On 10.11.7 that is only half the story, which is what reencodedStreams is
 * for.
 */
export function codecReasons(source: MediaSource): string[] {
	const fromUrl = new URLSearchParams(
		(source.TranscodingUrl ?? "").split("?")[1] ?? "",
	);
	const reasons = [
		...(source.TranscodeReasons ?? []),
		...(fromUrl.get("TranscodeReasons")?.split(",") ?? []),
	];

	return reasons
		.map((r) => r.trim())
		.filter((r) => r && r !== "DirectPlayError");
}

export type PlaybackInfoBody = {
	ErrorCode?: string | null;
	MediaSources: MediaSource[];
};

export async function playbackInfoBody(
	api: APIRequestContext,
	itemId: string,
	deviceProfile: Profile,
	mediaSourceId?: string,
): Promise<PlaybackInfoBody> {
	const user = await userId(api);
	const response = await api.post(
		`/Items/${itemId}/PlaybackInfo?userId=${user}`,
		{
			data: {
				UserId: user,
				AutoOpenLiveStream: false,
				MaxStreamingBitrate: deviceProfile.MaxStreamingBitrate,
				DeviceProfile: deviceProfile,
				...(mediaSourceId ? { MediaSourceId: mediaSourceId } : {}),
			},
		},
	);
	expect(
		response.ok(),
		`PlaybackInfo for ${deviceProfile.Name} failed: ${response.status()}`,
	).toBeTruthy();
	return await response.json();
}

export async function playbackInfoFor(
	api: APIRequestContext,
	itemId: string,
	deviceProfile: Profile,
): Promise<MediaSource> {
	return (await playbackInfoBody(api, itemId, deviceProfile)).MediaSources[0];
}
