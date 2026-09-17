import { APIRequestContext, expect, test } from "@playwright/test";
import {
	apiContext,
	channelItems,
	loginUi,
	setPluginConfig,
	userId,
} from "../helpers/jellyfin";

async function talkNamed(api: APIRequestContext, name: string) {
	const byYear = (await channelItems(api)).find((i) =>
		i.Name.includes("Browse by Year"),
	)!;
	const years = await channelItems(api, byYear.Id);
	const conferences = await channelItems(api, years[0].Id);
	const talks = await channelItems(api, conferences[0].Id);
	return talks.find((t) => t.Name === name)!;
}

async function playbackInfo(api: APIRequestContext, itemId: string) {
	const user = await userId(api);
	const response = await api.post(
		`/Items/${itemId}/PlaybackInfo?userId=${user}`,
		{
			data: { UserId: user, AutoOpenLiveStream: false },
		},
	);
	expect(response.ok()).toBeTruthy();
	const body = await response.json();
	return { ...body.MediaSources[0], PlaySessionId: body.PlaySessionId };
}

test.describe("playback", () => {
	test("MP4 with an extra video stream keeps all streams and the right audio index", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");

		const source = await playbackInfo(api, talk.Id);

		expect(source.Path).toContain("/api/ChaosflixStream/proxy/");
		expect(source.Container).toBe("mp4");
		expect(source.SupportsDirectStream).toBe(true);
		expect(source.IsRemote).toBe(false);

		const streams = source.MediaStreams.map(
			(s: { Type: string; Index: number }) => `${s.Type}@${s.Index}`,
		);
		expect(streams).toEqual(["Video@0", "Video@1", "Audio@2"]);
		expect(source.DefaultAudioStreamIndex).toBe(2);
	});

	test("proxy serves the recording with range support", async () => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");
		const source = await playbackInfo(api, talk.Id);
		const proxyPath =
			new URL(source.Path).pathname + new URL(source.Path).search;

		const full = await api.get(proxyPath);
		expect(full.status()).toBe(200);
		expect(full.headers()["accept-ranges"]).toBe("bytes");
		expect(full.headers()["content-type"]).toContain("video/");

		const partial = await api.get(proxyPath, {
			headers: { Range: "bytes=0-99" },
		});
		expect(partial.status()).toBe(206);
		expect(partial.headers()["content-range"]).toMatch(/^bytes 0-99\/\d+$/);
		expect((await partial.body()).length).toBe(100);
	});

	test("playback is recorded in the watch history", async () => {
		const api = await apiContext();
		const user = await userId(api);
		const talk = await talkNamed(api, "Three stream talk");
		const source = await playbackInfo(api, talk.Id);

		const session = {
			ItemId: talk.Id,
			MediaSourceId: source.Id,
			PlaySessionId: source.PlaySessionId,
			CanSeek: true,
		};
		await api.post("/Sessions/Playing", {
			data: { ...session, PositionTicks: 0 },
		});
		// Jellyfin keeps a resume position only for items longer than five
		// minutes, so the 5s fixture is watched to the end instead.
		await api.post("/Sessions/Playing/Progress", {
			data: { ...session, PositionTicks: 30_000_000, IsPaused: false },
		});
		await api.post("/Sessions/Playing/Stopped", {
			data: { ...session, PositionTicks: 49_000_000 },
		});

		await expect
			.poll(async () => {
				const item = await (
					await api.get(`/Users/${user}/Items/${talk.Id}`)
				).json();
				return item.UserData?.Played;
			})
			.toBe(true);
	});
});
