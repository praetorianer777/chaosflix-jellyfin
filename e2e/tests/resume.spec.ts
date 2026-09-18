import { APIRequestContext, expect, test } from "@playwright/test";
import {
	ANDROID_CLIENT,
	apiContext,
	clientContext,
	playbackInfo,
	setPluginConfig,
	talkNamed,
	userId,
	WEB_CLIENT,
} from "../helpers/jellyfin";
import { ANDROID_EXOPLAYER, BROWSER_WITHOUT_H264 } from "../helpers/profiles";

// Resume is a server-side contract: whatever one client reports has to be
// visible to the next one as UserData.PlaybackPositionTicks and turn up in that
// user's resume list. Both clients here are the same user on different devices
// (different DeviceId, different device profile), which is how Jellyfin tells
// them apart — and the direction that broke between v0.0.26 and v0.0.29 was
// always only one of the two.
//
// "Long talk" is the fixture over Jellyfin's five minute MinResumeDurationSeconds;
// for anything shorter the position is discarded, which is why the short
// fixtures can only assert the watched state (playback.spec.ts).

const TALK = "Long talk";
const seconds = (value: number) => value * 10_000_000;

type Session = {
	ItemId: string;
	MediaSourceId: string;
	PlaySessionId: string;
	CanSeek: boolean;
};

async function openSession(
	api: APIRequestContext,
	itemId: string,
	deviceProfile: { MaxStreamingBitrate: number },
	fromTicks: number,
): Promise<Session> {
	const source = await playbackInfo(api, itemId, deviceProfile);
	const session = {
		ItemId: itemId,
		MediaSourceId: source.Id,
		PlaySessionId: source.PlaySessionId,
		CanSeek: true,
	};
	const started = await api.post("/Sessions/Playing", {
		data: { ...session, PositionTicks: fromTicks },
	});
	expect(started.ok(), `reporting playback start: ${started.status()}`).toBe(
		true,
	);
	return session;
}

async function playUntil(
	api: APIRequestContext,
	session: Session,
	positionTicks: number,
): Promise<void> {
	await api.post("/Sessions/Playing/Progress", {
		data: { ...session, PositionTicks: positionTicks, IsPaused: false },
	});
	await api.post("/Sessions/Playing/Stopped", {
		data: { ...session, PositionTicks: positionTicks },
	});
}

async function resumePosition(
	api: APIRequestContext,
	itemId: string,
): Promise<number | undefined> {
	const item = await (
		await api.get(`/Users/${await userId(api)}/Items/${itemId}`)
	).json();
	return item.UserData?.PlaybackPositionTicks;
}

async function resumeList(api: APIRequestContext): Promise<string[]> {
	const response = await api.get(
		`/UserItems/Resume?userId=${await userId(api)}&limit=50`,
	);
	expect(response.ok(), `resume query failed: ${response.status()}`).toBe(true);
	const body = await response.json();
	return (body.Items as Array<{ Name: string }>).map((item) => item.Name);
}

test.describe("resume across clients", () => {
	let web: APIRequestContext;
	let android: APIRequestContext;
	let talkId: string;

	test.beforeAll(async () => {
		const api = await apiContext();
		// The long talk is published as MP4 only; the format switching in
		// profiles.spec.ts drops the channel's media source cache (#36), so the
		// setting is put back where this spec needs it rather than assumed.
		await setPluginConfig(api, { PreferredFormat: "Mp4" });
		talkId = (await talkNamed(api, TALK)).Id;
		web = await clientContext(WEB_CLIENT);
		android = await clientContext(ANDROID_CLIENT);
	});

	test.beforeEach(async () => {
		const api = await apiContext();
		const response = await api.delete(
			`/UserPlayedItems/${talkId}?userId=${await userId(api)}`,
		);
		expect(response.ok(), "clearing the watch state failed").toBe(true);
	});

	test("the talk is long enough for Jellyfin to keep a position at all", async () => {
		const api = await apiContext();
		const details = await (
			await api.get(`/Users/${await userId(api)}/Items/${talkId}`)
		).json();

		// Below this the position is silently thrown away and every assertion
		// below would pass for the wrong reason.
		expect(details.RunTimeTicks).toBeGreaterThan(seconds(300));
	});

	test("a talk started in the browser resumes on Android", async () => {
		const session = await openSession(web, talkId, BROWSER_WITHOUT_H264, 0);
		await playUntil(web, session, seconds(120));

		await expect.poll(() => resumePosition(android, talkId)).toBe(seconds(120));
		expect(await resumeList(android)).toContain(TALK);
	});

	test("a talk continued on Android resumes in the browser", async () => {
		const browser = await openSession(web, talkId, BROWSER_WITHOUT_H264, 0);
		await playUntil(web, browser, seconds(90));
		const handedOver = (await resumePosition(android, talkId))!;
		expect(handedOver).toBe(seconds(90));

		// Android picks the talk up where the browser left it, with its own
		// session and its own media source, and watches on.
		const phone = await openSession(
			android,
			talkId,
			ANDROID_EXOPLAYER,
			handedOver,
		);
		expect(phone.MediaSourceId).toBeTruthy();
		await playUntil(android, phone, seconds(240));

		await expect.poll(() => resumePosition(web, talkId)).toBe(seconds(240));
		expect(await resumeList(web)).toContain(TALK);
	});

	test("watching it to the end clears the resume offer for both clients", async () => {
		const session = await openSession(web, talkId, BROWSER_WITHOUT_H264, 0);
		await playUntil(web, session, seconds(325));

		await expect.poll(() => resumePosition(android, talkId)).toBe(0);
		expect(await resumeList(android)).not.toContain(TALK);
		expect(await resumeList(web)).not.toContain(TALK);
	});
});
