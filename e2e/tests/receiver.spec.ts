import { expect, test } from "@playwright/test";
import {
	apiContext,
	movieFromFile,
	readToken,
	talkNamed,
} from "../helpers/jellyfin";
import {
	buildReceiver,
	type CastItem,
	type ReceiverRun,
	castAndWait,
	milestones,
	playMethod,
	startReceiver,
	streamUrl,
} from "../helpers/receiver";
import { JELLYFIN_URL } from "../playwright.config";

/**
 * Runs the real jellyfin-chromecast bundle against this stack (#55). Every
 * earlier test replayed the receiver's HTTP calls by hand; this one executes
 * the receiver's own JavaScript, so a client-side failure while preparing
 * playback — which looks exactly like the reported endless spinner — would show
 * up here as an exception or as a run that never reaches the player.
 *
 * Opt-in: the first run clones and builds jellyfin-chromecast from the network,
 * which has no business gating a push.
 */
test.describe("jellyfin-chromecast receiver", () => {
	/** The plain h264/aac fixture, as an ordinary library item. */
	const MOVIE = "two-stream.mp4";

	test.skip(
		!process.env.RECEIVER_E2E,
		"set RECEIVER_E2E=1 (clones and builds jellyfin-chromecast)",
	);
	test.describe.configure({ timeout: 300_000 });

	test.beforeAll(() => {
		buildReceiver();
	});

	const report = (label: string, run: ReceiverRun) => {
		console.log(`\n── ${label} ──`);
		console.log("milestones:", milestones(run));
		console.log("playMethod:", playMethod(run));
		console.log("url:", streamUrl(run));
		console.log(
			"server calls:",
			run.requests.filter((r) => r.includes(JELLYFIN_URL)),
		);
		console.log(
			"bus:",
			run.busMessages.map((m) => m.message?.type ?? m.message),
		);
		console.log(
			"direct play profiles:",
			run.playbackInfo.flatMap((body) =>
				(JSON.parse(body).DeviceProfile?.DirectPlayProfiles ?? []).filter(
					(p: { Type: string }) => p.Type === "Video",
				),
			),
		);
		console.log("uncaught:", run.errors);
		console.log(
			"console:",
			run.console.map((c) => `${c.level}: ${c.text}`.slice(0, 300)).join("\n"),
		);
	};

	const cast = async (
		page: import("@playwright/test").Page,
		item: CastItem,
		maxBitrate: number,
	) => {
		const recorder = await startReceiver(page);

		return castAndWait(
			page,
			{
				accessToken: readToken(),
				command: "PlayNow",
				maxBitrate,
				options: { items: [item], startPositionTicks: 0 },
				receiverName: "Harness",
				serverAddress: JELLYFIN_URL,
			},
			recorder,
		);
	};

	/** What the receiver handed its player, fetched from the receiver's page. */
	const fetchStatus = (page: import("@playwright/test").Page, url: string) =>
		page.evaluate(async (target) => (await fetch(target)).status, url);

	const expectPlayable = (run: ReceiverRun, label: string) => {
		expect(run.errors, `${label}: the receiver threw`).toEqual([]);
		expect(
			run.busMessages.filter((m) =>
				["connectionerror", "error", "playbackerror"].includes(m.message?.type),
			),
			`${label}: the receiver reported an error to the sender`,
		).toEqual([]);
		expect(milestones(run), `${label}: the receiver stopped early`).toEqual({
			createStreamInfo: true,
			getItem: true,
			getMaxBitrate: true,
			getPlaybackInfo: true,
			playerLoad: true,
		});
		expect(streamUrl(run), `${label}: no url reached the player`).toBeTruthy();
	};

	test("a Chaosflix talk reaches the player", async ({ page }) => {
		const api = await apiContext();
		const talk = await talkNamed(api, "Three stream talk");

		const run = await cast(
			page,
			{
				Id: talk.Id,
				IsFolder: false,
				MediaType: "Video",
				Name: talk.Name,
				Type: talk.Type,
			},
			120_000_000,
		);

		report("channel item (talk)", run);
		expectPlayable(run, "talk");
		// A talk is never direct play, so the receiver has to follow the HLS
		// transcoding url — the path that was never observed running.
		expect(playMethod(run)).toBe("Transcode");
		expect(streamUrl(run)).toContain("master.m3u8");
		expect(await fetchStatus(page, streamUrl(run)!)).toBe(200);
	});

	test("a local movie reaches the player by direct play", async ({ page }) => {
		const api = await apiContext();
		const movie = await movieFromFile(api, MOVIE);

		const run = await cast(
			page,
			{
				Id: movie.Id,
				IsFolder: false,
				MediaType: "Video",
				Name: movie.Name,
				Type: movie.Type,
			},
			120_000_000,
		);

		report("library item, high bitrate", run);
		expectPlayable(run, "movie");
		// The receiver refuses direct play on a non-http source, so an ordinary
		// local movie it can decode comes out as a static /videos/…/stream url.
		expect(playMethod(run)).toBe("DirectStream");
		expect(await fetchStatus(page, streamUrl(run)!)).toBe(200);
	});

	test("a local movie reaches the player when forced to transcode", async ({
		page,
	}) => {
		const api = await apiContext();
		const movie = await movieFromFile(api, MOVIE);

		// Below what the fixture needs, so the server has to answer with HLS:
		// this is the control for "is the distinguishing factor the channel item
		// or the transcode?".
		const run = await cast(
			page,
			{
				Id: movie.Id,
				IsFolder: false,
				MediaType: "Video",
				Name: movie.Name,
				Type: movie.Type,
			},
			100_000,
		);

		report("library item, forced transcode", run);
		expectPlayable(run, "transcoding movie");
		expect(playMethod(run)).toBe("Transcode");
		expect(streamUrl(run)).toContain("master.m3u8");
		expect(await fetchStatus(page, streamUrl(run)!)).toBe(200);
	});
});
