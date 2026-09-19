import { expect, test } from "@playwright/test";
import {
	apiContext,
	playbackInfo,
	talkNamed,
	userId,
} from "../helpers/jellyfin";
import { ANDROID_EXOPLAYER, playbackInfoFor } from "../helpers/profiles";

type Stream = {
	Type: string;
	Index: number;
	Codec: string;
	Language: string;
	IsExternal: boolean;
	IsTextSubtitleStream: boolean;
	DeliveryMethod: string;
	DeliveryUrl: string;
	IsExternalUrl: boolean;
	Path: string;
};

const subtitlesOf = (source: { MediaStreams: Stream[] }) =>
	source.MediaStreams.filter((s) => s.Type === "Subtitle");

test.describe("subtitles", () => {
	test("a talk's subtitle recordings become external subtitle streams", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfo(api, talk.Id);
		const subtitles = subtitlesOf(source);

		expect(subtitles.map((s) => `${s.Language}/${s.Codec}`)).toEqual([
			"eng/vtt",
			"fin/srt",
		]);
		// The probed video and audio streams keep 0..2, and the subtitle language
		// is the subtitle's own, not the German video's.
		expect(subtitles.map((s) => s.Index)).toEqual([3, 4]);
		for (const subtitle of subtitles) {
			expect(subtitle.IsExternal).toBe(true);
			expect(subtitle.IsTextSubtitleStream).toBe(true);
			expect(subtitle.DeliveryMethod).toBe("External");
			expect(subtitle.DeliveryUrl).toContain("/api/ChaosflixStream/proxy/");
		}
	});

	test("the subtitle url returns the subtitle bytes", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const source = await playbackInfo(api, talk.Id);

		for (const [subtitle, type, body] of [
			[subtitlesOf(source)[0], "text/vtt", "WEBVTT"],
			[subtitlesOf(source)[1], "application/x-subrip", "Suomenkieliset"],
		] as const) {
			const url = new URL(subtitle.DeliveryUrl);
			const response = await api.get(url.pathname + url.search);

			expect(response.status()).toBe(200);
			expect(response.headers()["content-type"]).toContain(type);
			expect(await response.text()).toContain(body);
		}
	});

	test("an unsigned subtitle url is refused", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const source = await playbackInfo(api, talk.Id);
		const path = new URL(subtitlesOf(source)[0].DeliveryUrl).pathname;

		const forged = path.replace(
			/\/proxy\/([^/]+)\/([^/]+)\//,
			"/proxy/$1/" + "0".repeat(64) + "/",
		);
		expect((await api.get(forged)).status()).toBe(401);
	});

	test("a real client profile is handed the plugin's own url", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfoFor(api, talk.Id, ANDROID_EXOPLAYER);
		const subtitles = subtitlesOf(source as unknown as { MediaStreams: Stream[] });

		// Jellyfin only passes an external url through when the stream's codec is
		// spelled the way the client's subtitle profile spells the format — "vtt"
		// and "srt", not the ffmpeg names. Otherwise it replaces the url with its
		// own /Videos/…/Subtitles route, which cannot read a remote path.
		for (const subtitle of subtitles) {
			expect(subtitle.DeliveryMethod).toBe("External");
			expect(subtitle.IsExternalUrl).toBe(true);
			expect(subtitle.DeliveryUrl).toContain("/api/ChaosflixStream/proxy/");
		}
	});

	test("subtitles do not disturb the version order", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const user = await userId(api);

		const response = await api.post(
			`/Items/${talk.Id}/PlaybackInfo?userId=${user}`,
			{ data: { UserId: user, AutoOpenLiveStream: false } },
		);
		const sources = (await response.json()).MediaSources as {
			Name: string;
			MediaStreams: Stream[];
		}[];

		// Every version carries the captions, and only the preferred one declares
		// a video stream, so Jellyfin's sort by video width leaves it in front (#69).
		expect(sources[0].Name).toBe("HD MP4 · Deutsch");
		for (const source of sources) {
			expect(subtitlesOf(source).map((s) => s.Language)).toEqual([
				"eng",
				"fin",
			]);
		}
		expect(
			sources.slice(1).flatMap((s) => s.MediaStreams.map((m) => m.Type)),
		).not.toContain("Video");
	});

	test("a talk without subtitles declares none", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Two stream talk");

		const source = await playbackInfo(api, talk.Id);

		expect(subtitlesOf(source)).toEqual([]);
	});
});
