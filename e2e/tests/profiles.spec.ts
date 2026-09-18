import { expect, test } from "@playwright/test";
import {
	apiContext,
	setPluginConfig,
	talkNamed,
	userId,
} from "../helpers/jellyfin";
import {
	ANDROID_EXOPLAYER,
	BROWSER_WITHOUT_H264,
	BROWSER_WITH_H264,
	CHROMECAST,
	codecReasons,
	playbackInfoBody,
	playbackInfoFor,
} from "../helpers/profiles";

// Fixing playback for one client kept breaking another (v0.0.26–v0.0.29).
// Jellyfin derives what it does with a stream from the client's device
// profile, so asking it with each client shape turns that flip-flop into a
// failing test within seconds — no Chromecast or phone needed (#34).
// All of these use "Three stream talk". Jellyfin caches the media sources it
// got for a talk, but the channel drops that cache when the configuration
// changes (#36), so switching the format below reaches this talk too.
test.describe("client profiles", () => {
	test.afterAll(async () => {
		await setPluginConfig(await apiContext(), { PreferredFormat: "Mp4" });
	});

	test("Chromecast gets the MP4 without anything being re-encoded", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfoFor(api, talk.Id, CHROMECAST);

		expect(source.Container).toBe("mp4");
		expect(source.Path).toContain("/api/ChaosflixStream/proxy/");
		expect(source.IsRemote).toBe(false);
		expect(codecReasons(source)).toEqual([]);
	});

	test("Android gets the real audio index on a three stream MP4", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfoFor(api, talk.Id, ANDROID_EXOPLAYER);

		// The extra video stream for the visually impaired pushes audio to 2;
		// getting this wrong is what silenced playback in v0.0.28.
		expect(source.MediaStreams.map((s) => `${s.Type}@${s.Index}`)).toEqual([
			"Video@0",
			"Video@1",
			"Audio@2",
		]);
		expect(source.DefaultAudioStreamIndex).toBe(2);
		expect(codecReasons(source)).toEqual([]);
	});

	test("a browser with H.264 plays the same MP4 without re-encoding", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfoFor(api, talk.Id, BROWSER_WITH_H264);

		expect(codecReasons(source)).toEqual([]);
	});

	test("a browser without H.264 re-encodes, and names the codec as the reason", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfoFor(api, talk.Id, BROWSER_WITHOUT_H264);

		// Re-encoding is correct here — the client cannot decode H.264. What
		// matters is that the codec is the reason, not a broken media source.
		expect(codecReasons(source)).toContain("VideoCodecNotSupported");
	});

	test("that same browser gets WebM untouched once the format is switched", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "WebM" });
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfoFor(api, talk.Id, BROWSER_WITHOUT_H264);

		expect(source.Container).toBe("webm");
		expect(codecReasons(source)).toEqual([]);
	});

	test("switching the format does not disturb the other clients", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "WebM" });
		const talk = await talkNamed(api, "Three stream talk");

		// Android plays both, so it must stay free of re-encoding either way.
		const webm = await playbackInfoFor(api, talk.Id, ANDROID_EXOPLAYER);
		expect(webm.Container).toBe("webm");
		expect(codecReasons(webm)).toEqual([]);

		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const mp4 = await playbackInfoFor(api, talk.Id, ANDROID_EXOPLAYER);
		expect(mp4.Container).toBe("mp4");
		expect(codecReasons(mp4)).toEqual([]);
		// Stale probe data would describe the WebM here (#3).
		expect(mp4.DefaultAudioStreamIndex).toBe(2);
	});
});

// Every recording of a talk is offered as its own version, ordered by the
// configured preference (#69). The order is what keeps the default pick the
// same for anyone who never opens the selector.
test.describe("selectable versions", () => {
	test.afterAll(async () => {
		await setPluginConfig(await apiContext(), { PreferredFormat: "Mp4" });
	});

	test("every recording is offered, the preferred one first", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");

		const body = await playbackInfoBody(api, talk.Id, ANDROID_EXOPLAYER);

		expect(body.MediaSources.map((s) => s.Name)).toEqual([
			"HD MP4 · Deutsch",
			"HD MP4 · English",
			"SD MP4 · Deutsch",
			"HD WebM · Deutsch",
		]);
		expect(new Set(body.MediaSources.map((s) => s.Id)).size).toBe(4);
		for (const source of body.MediaSources) {
			expect(source.Path).toContain("/api/ChaosflixStream/proxy/");
			expect(source.Path).toMatch(/[?&]t=[0-9a-f]+/);
		}
		// The first one is still what a client that never asks gets, and it is
		// the only one that costs a probe: the others are described by what the
		// API says about them until they are preferred themselves.
		expect(body.MediaSources[0].Container).toBe("mp4");
		expect(codecReasons(body.MediaSources[0])).toEqual([]);
		expect(body.MediaSources[0].MediaStreams).toHaveLength(3);
		for (const other of body.MediaSources.slice(1)) {
			expect(other.MediaStreams).toHaveLength(0);
		}
	});

	test("the English version is selectable and serves its own recording", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");

		const versions = (await playbackInfoBody(api, talk.Id, ANDROID_EXOPLAYER))
			.MediaSources;
		const english = versions.find((s) => s.Name === "HD MP4 · English")!;
		expect(english.Id).not.toBe(versions[0].Id);

		const picked = await playbackInfoBody(
			api,
			talk.Id,
			ANDROID_EXOPLAYER,
			english.Id,
		);

		expect(picked.ErrorCode ?? null).toBeNull();
		expect(picked.MediaSources).toHaveLength(1);
		expect(picked.MediaSources[0].Id).toBe(english.Id);
		expect(picked.MediaSources[0].Path).toContain("language=eng");

		// Playable from both ends: the proxy behind that version serves the
		// recording, and Jellyfin hands the client a stream for it. Without a
		// probed layout it is a transcode rather than a direct stream — the
		// price of not probing every version of every talk.
		const url = new URL(picked.MediaSources[0].Path);
		const played = await api.get(url.pathname + url.search, {
			headers: { Range: "bytes=0-99" },
		});
		expect(played.status()).toBe(206);
		expect(played.headers()["content-type"]).toContain("video/");

		const stream = await api.get(picked.MediaSources[0].TranscodingUrl!);
		expect(stream.status()).toBe(200);
		expect(await stream.text()).toContain("#EXTM3U");
	});

	test("switching the preference reorders what a client sees", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "WebM" });
		const talk = await talkNamed(api, "Three stream talk");

		// Jellyfin caches a talk's media sources for five minutes; the plugin
		// drops that cache when the configuration changes (#36).
		const webmFirst = await playbackInfoBody(api, talk.Id, ANDROID_EXOPLAYER);
		expect(webmFirst.MediaSources[0].Name).toBe("HD WebM · Deutsch");
		expect(webmFirst.MediaSources[0].Container).toBe("webm");

		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const mp4First = await playbackInfoBody(api, talk.Id, ANDROID_EXOPLAYER);
		expect(mp4First.MediaSources[0].Name).toBe("HD MP4 · Deutsch");
		expect(mp4First.MediaSources.map((s) => s.Name).sort()).toEqual(
			webmFirst.MediaSources.map((s) => s.Name).sort(),
		);
	});
});

// jellyfin-androidtv builds its PlaybackInfo request from the media source it
// finds on the item DTO. For a channel item Jellyfin puts a placeholder there
// whose id is the item id — the real sources exist only in the PlaybackInfo
// answer, and the placeholder is dropped from it. So the id the app sends back
// has to be one the plugin hands out, or PlaybackInfo answers NoCompatibleStream
// with no sources and the app spins forever without telling anyone (#55).
test.describe("Android TV client", () => {
	test("the media source id on the item DTO is one PlaybackInfo accepts", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		const talk = await talkNamed(api, "Three stream talk");
		const user = await userId(api);

		const item = await (
			await api.get(`/Users/${user}/Items/${talk.Id}`)
		).json();
		const fromDto = item.MediaSources[0].Id;
		expect(fromDto).toBe(talk.Id);

		// Offering several versions must not move that id off the first one.
		const all = await playbackInfoBody(api, talk.Id, ANDROID_EXOPLAYER);
		expect(all.MediaSources.length).toBeGreaterThan(1);
		expect(all.MediaSources[0].Id).toBe(fromDto);

		const body = await playbackInfoBody(
			api,
			talk.Id,
			ANDROID_EXOPLAYER,
			fromDto,
		);

		expect(body.ErrorCode ?? null).toBeNull();
		expect(body.MediaSources).toHaveLength(1);
		expect(body.MediaSources[0].Id).toBe(fromDto);
		expect(codecReasons(body.MediaSources[0])).toEqual([]);
	});
});
