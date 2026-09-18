import { expect, test } from "@playwright/test";
import {
	apiContext,
	channelItems,
	clearWatchState,
	openSession,
	playUntil,
	setPluginConfig,
	talkNamed,
	userData,
} from "../helpers/jellyfin";
import { BROWSER_WITHOUT_H264 } from "../helpers/profiles";

// A talk listed in several folders needs one channel item id per folder (#15),
// and Jellyfin stores each of those as a library item with its own user data.
// Watch state therefore has to be mirrored between them, or resetting progress
// on the copy in front of the user leaves the other copies untouched (#53).
//
// "Long talk" is the fixture over Jellyfin's five minute MinResumeDurationSeconds;
// below that the position is discarded.

const TALK = "Long talk";
const seconds = (value: number) => value * 10_000_000;

test.describe("watch state across the copies of a talk", () => {
	let conferenceCopy: string;
	let popularCopy: string;

	test.beforeAll(async () => {
		const api = await apiContext();
		// The long talk is published as MP4 only, and profiles.spec.ts leaves the
		// configuration on whatever it tested last.
		await setPluginConfig(api, { PreferredFormat: "Mp4" });

		conferenceCopy = (await talkNamed(api, TALK)).Id;
		const popular = (await channelItems(api)).find((i) =>
			i.Name.includes("Popular"),
		)!;
		popularCopy = (await channelItems(api, popular.Id)).find(
			(t) => t.Name === TALK,
		)!.Id;
	});

	test.beforeEach(async () => {
		const api = await apiContext();
		await clearWatchState(api, conferenceCopy);
		await clearWatchState(api, popularCopy);
	});

	test("the two folders hand out two separate library items", async () => {
		expect(popularCopy).not.toBe(conferenceCopy);
	});

	test("progress reported on one copy shows on the other", async () => {
		const api = await apiContext();
		const session = await openSession(
			api,
			conferenceCopy,
			BROWSER_WITHOUT_H264,
			0,
		);
		await playUntil(api, session, seconds(120));

		await expect
			.poll(
				async () => (await userData(api, popularCopy)).PlaybackPositionTicks,
			)
			.toBe(seconds(120));
	});

	test("clearing the watch state on one copy clears it on the other", async () => {
		const api = await apiContext();
		const session = await openSession(
			api,
			popularCopy,
			BROWSER_WITHOUT_H264,
			0,
		);
		await playUntil(api, session, seconds(150));
		await expect
			.poll(
				async () => (await userData(api, conferenceCopy)).PlaybackPositionTicks,
			)
			.toBe(seconds(150));

		await clearWatchState(api, conferenceCopy);

		await expect
			.poll(async () => await userData(api, popularCopy))
			.toMatchObject({ PlaybackPositionTicks: 0, Played: false });
	});

	test("marking one copy watched marks the other watched", async () => {
		const api = await apiContext();
		const session = await openSession(
			api,
			conferenceCopy,
			BROWSER_WITHOUT_H264,
			0,
		);
		await playUntil(api, session, seconds(325));

		await expect
			.poll(async () => (await userData(api, popularCopy)).Played)
			.toBe(true);
	});
});
