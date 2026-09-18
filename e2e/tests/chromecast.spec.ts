import { expect, test } from "@playwright/test";
import {
	castClient,
	castStreamUrl,
	fetchCastMedia,
	receiverClient,
} from "../helpers/chromecast";
import { apiContext, setPluginConfig, talkNamed } from "../helpers/jellyfin";
import { CHROMECAST, playbackInfoFor } from "../helpers/profiles";

/**
 * profiles.spec.ts stops at the decision Jellyfin makes for a Chromecast and
 * never fetches a byte, so everything past that decision was untested (#55).
 * A cast device is not the sender: it opens the url itself, from its own place
 * in the network and without the sender's session. These tests follow the url
 * a receiver would pick all the way down to media bytes, over a client that
 * carries no Jellyfin credentials.
 */
test.describe("Chromecast playback", () => {
	test.beforeEach(async () => {
		await setPluginConfig(await apiContext(), { PreferredFormat: "Mp4" });
	});

	test("the stream a Chromecast is handed delivers real bytes", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const source = await playbackInfoFor(api, talk.Id, CHROMECAST);

		const stream = castStreamUrl(source, talk.Id);
		const media = await fetchCastMedia(await castClient(), stream);

		expect(
			media.status,
			`${stream.playMethod} for a Chromecast answered ${media.status} from ${media.url}`,
		).toBe(200);
		expect(media.contentType).not.toContain("application/json");
		expect(
			media.bytes,
			`${stream.playMethod} for a Chromecast answered with an empty body from ${media.url}`,
		).toBeGreaterThan(0);
	});

	test("a Chromecast is never sent to the proxy url itself", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const source = await playbackInfoFor(api, talk.Id, CHROMECAST);

		// MediaSourceInfo.Path is built from GetSmartApiUrl, which knows nothing
		// about who asked: it is the server's own published or bind address, so
		// it is only guaranteed to resolve on the server. ffmpeg fetching it
		// from inside is fine; a cast device opening it is not, and the receiver
		// uses Path verbatim the moment direct play is on. Keeping direct play
		// off is what keeps that address away from the device (#55).
		expect(castStreamUrl(source, talk.Id).playMethod).not.toBe("DirectPlay");
		expect(source.SupportsDirectPlay).toBe(false);
	});

	// Everything the receiver does between being told to play and asking for a
	// media source, in the shape jellyfin-chromecast sends it: the item lookup
	// of onStopPlayerBeforePlaybackDone and the PlaybackInfo of
	// playItemInternal, neither of which carries a user id. A talk is a channel
	// item, so it is served by the plugin rather than from the library, and a
	// server that wanted a user id here would fail the receiver alone (#55).
	test("the receiver reaches a playable source without sending a user id", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const receiver = await receiverClient();

		const lookup = await receiver.get(`/Items/${talk.Id}`);
		expect(lookup.status()).toBe(200);
		const item = await lookup.json();
		// The receiver hands this straight to playItemInternal and reads only
		// these; a channel item has to look like any other video here.
		expect(item.Id).toBe(talk.Id);
		expect(item.MediaType).toBe("Video");
		expect(item.IsFolder).toBe(false);
		expect(item.ServerId).toBeTruthy();

		const response = await receiver.post(`/Items/${talk.Id}/PlaybackInfo`, {
			data: {
				DeviceProfile: CHROMECAST,
				MaxStreamingBitrate: CHROMECAST.MaxStreamingBitrate,
				StartTimeTicks: 0,
			},
		});
		expect(response.status()).toBe(200);
		const info = await response.json();
		expect(info.ErrorCode ?? null).toBeNull();
		expect(info.PlaySessionId).toBeTruthy();

		// getOptimalMediaSource falls through to transcoding, createStreamInfo
		// recognises HLS by TranscodingSubProtocol alone and dereferences
		// TranscodingUrl unchecked, and createMediaInformation takes the
		// duration from the source rather than from the item.
		const source = info.MediaSources[0];
		expect(source.SupportsTranscoding).toBe(true);
		expect(source.TranscodingSubProtocol).toBe("hls");
		expect(source.TranscodingUrl).toBeTruthy();
		expect(source.RunTimeTicks).toBeGreaterThan(0);
	});

	test("the url does not depend on which client asked first", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");

		// Jellyfin caches channel media sources server-wide for five minutes,
		// keyed by the item, so the url computed while serving one client is
		// handed to the next. That is only safe because the plugin builds it
		// without reference to the caller; if it ever started resolving per
		// client, the second client here would get the first one's address.
		const first = await playbackInfoFor(api, talk.Id, CHROMECAST);
		const second = await playbackInfoFor(
			await apiContext(),
			talk.Id,
			CHROMECAST,
		);

		expect(new URL(second.Path).origin).toBe(new URL(first.Path).origin);
	});
});
