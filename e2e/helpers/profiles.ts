import { APIRequestContext, expect } from "@playwright/test";
import { userId } from "./jellyfin";

/**
 * Device profiles shaped like the clients this plugin has broken on before.
 * Jellyfin decides direct stream vs transcode from these, so asking it with
 * each profile catches a fix for one client breaking another — which is what
 * kept happening between v0.0.26 and v0.0.29.
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
	SubtitleProfiles: [],
	MaxStreamingBitrate: 120_000_000,
});

export const CHROMECAST = profile(
	"Chromecast",
	[["mp4", "h264", "aac"]],
	[["ts", "h264", "aac"]],
);

export const ANDROID_EXOPLAYER = profile(
	"Android ExoPlayer",
	[
		["mp4", "h264", "aac"],
		["webm", "vp9", "opus"],
	],
	[["ts", "h264", "aac"]],
);

/** Chromium as Playwright ships it: VP9/Opus only, no H.264. */
export const BROWSER_WITHOUT_H264 = profile(
	"Chromium without H.264",
	[["webm", "vp9", "opus"]],
	[["ts", "h264", "aac"]],
);

export const BROWSER_WITH_H264 = profile(
	"Browser with H.264",
	[
		["mp4", "h264", "aac"],
		["webm", "vp9", "opus"],
	],
	[["ts", "h264", "aac"]],
);

export type MediaSource = {
	Path: string;
	Container: string;
	IsRemote: boolean;
	SupportsDirectStream: boolean;
	TranscodingUrl?: string | null;
	TranscodeReasons?: string[];
	DefaultAudioStreamIndex?: number;
	MediaStreams: Array<{ Type: string; Index: number }>;
};

/**
 * Why not assert SupportsDirectStream: as soon as a device profile is sent,
 * Jellyfin answers channel items with an HLS url, because the plugin marks the
 * source SupportsDirectPlay=false. What decides whether anything is actually
 * re-encoded is the transcode reason — "DirectPlayError" alone means ffmpeg
 * only remuxes, while a codec or container reason means a real re-encode. The
 * Android suite sees the same thing from the other side (IsVideoDirect=true).
 */
export function codecReasons(source: MediaSource): string[] {
	const fromUrl = new URLSearchParams((source.TranscodingUrl ?? "").split("?")[1] ?? "");
	const reasons = [
		...(source.TranscodeReasons ?? []),
		...(fromUrl.get("TranscodeReasons")?.split(",") ?? []),
	];

	return reasons.map((r) => r.trim()).filter((r) => r && r !== "DirectPlayError");
}

export async function playbackInfoFor(
	api: APIRequestContext,
	itemId: string,
	deviceProfile: Profile,
): Promise<MediaSource> {
	const user = await userId(api);
	const response = await api.post(
		`/Items/${itemId}/PlaybackInfo?userId=${user}`,
		{
			data: {
				UserId: user,
				AutoOpenLiveStream: false,
				MaxStreamingBitrate: deviceProfile.MaxStreamingBitrate,
				DeviceProfile: deviceProfile,
			},
		},
	);
	expect(
		response.ok(),
		`PlaybackInfo for ${deviceProfile.Name} failed: ${response.status()}`,
	).toBeTruthy();
	return (await response.json()).MediaSources[0];
}
