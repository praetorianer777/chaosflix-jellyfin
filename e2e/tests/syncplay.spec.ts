import { APIRequestContext, expect, test } from "@playwright/test";
import {
	ANDROID_CLIENT,
	apiContext,
	clientContext,
	talkNamed,
	WEB_CLIENT,
} from "../helpers/jellyfin";

// SyncPlay is advertised in the README and was never exercised (#31). What a
// REST test can see is the group forming around a Chaosflix talk and the server
// accepting a queue built from it: the per-tick position sync between the
// participants travels over the websocket, and asserting that here would test
// Jellyfin rather than the plugin. What is worth guarding is that a channel item
// is an acceptable queue entry at all — a talk whose media source Jellyfin
// cannot resolve is refused here, and that would be a plugin bug.

type Group = {
	GroupId: string;
	GroupName: string;
	State: string;
	Participants: string[];
};

/** A client only becomes a session — and only a session can join a group. */
async function session(api: APIRequestContext): Promise<void> {
	const response = await api.post("/Sessions/Capabilities/Full", {
		data: {
			PlayableMediaTypes: ["Video"],
			SupportedCommands: ["PlayState", "Play"],
			SupportsMediaControl: true,
		},
	});
	expect(
		response.ok(),
		`registering the session failed: ${response.status()}`,
	).toBe(true);
}

/**
 * A second person, because that is what SyncPlay is for. Two sessions of one user
 * would do as well, but the group only reports distinct user names, so with one
 * account there is nothing to observe.
 */
async function guestContext(admin: APIRequestContext): Promise<APIRequestContext> {
	const name = "e2e-guest";
	const password = "chaosflix-e2e-guest";

	const users = (await (await admin.get("/Users")).json()) as Array<{
		Id: string;
		Name: string;
	}>;
	let guest = users.find((u) => u.Name === name);

	if (!guest) {
		const created = await admin.post("/Users/New", {
			data: { Name: name, Password: password },
		});
		expect(created.ok(), `creating the guest: ${created.status()}`).toBe(true);
		guest = (await created.json()) as { Id: string; Name: string };

		// A fresh account may see neither the channel nor SyncPlay, depending on the
		// server's defaults; the test is about the plugin, not about those. The
		// endpoint replaces the whole policy, so it is read back and amended.
		const current = (await (
			await admin.get(`/Users/${guest.Id}`)
		).json()) as { Policy: Record<string, unknown> };
		const policy = await admin.post(`/Users/${guest.Id}/Policy`, {
			data: {
				...current.Policy,
				EnableAllChannels: true,
				EnableAllFolders: true,
				EnableMediaPlayback: true,
				SyncPlayAccess: "CreateAndJoinGroups",
			},
		});
		expect(policy.ok(), `granting the guest access: ${policy.status()}`).toBe(
			true,
		);
	}

	const anonymous = await apiContext("", ANDROID_CLIENT);
	const login = await anonymous.post("/Users/AuthenticateByName", {
		data: { Username: name, Pw: password },
	});
	expect(login.ok(), `guest login: ${login.status()}`).toBe(true);

	return apiContext((await login.json()).AccessToken as string, ANDROID_CLIENT);
}

test.describe("SyncPlay", () => {
	test("two clients share a group built from a Chaosflix talk", async () => {
		const web = await clientContext(WEB_CLIENT);
		const guest = await guestContext(await apiContext());
		await session(web);
		await session(guest);

		const talk = await talkNamed(await apiContext(), "Long talk");

		const created = await web.post("/SyncPlay/New", {
			data: { GroupName: "chaosflix-e2e" },
		});
		expect(created.ok(), `creating the group: ${created.status()}`).toBe(true);
		const group = (await created.json()) as Group;

		try {
			const joined = await guest.post("/SyncPlay/Join", {
				data: { GroupId: group.GroupId },
			});
			expect(joined.ok(), `joining the group: ${joined.status()}`).toBe(true);

			// Participants are distinct user names, so this is what shows that the
			// second person really is in the group and not merely allowed to ask.
			await expect
				.poll(async () => {
					const groups = (await (
						await web.get("/SyncPlay/List")
					).json()) as Group[];
					return groups
						.find((g) => g.GroupId === group.GroupId)
						?.Participants.slice()
						.sort();
				})
				.toEqual(["e2e-admin", "e2e-guest"]);

			const queued = await web.post("/SyncPlay/SetNewQueue", {
				data: {
					PlayingQueue: [talk.Id],
					PlayingItemPosition: 0,
					StartPositionTicks: 0,
				},
			});
			expect(
				queued.ok(),
				`the server refused a talk as a queue entry: ${queued.status()}`,
			).toBe(true);

			const playing = await web.post("/SyncPlay/Unpause");
			expect(playing.ok(), `starting playback: ${playing.status()}`).toBe(true);

			// The group leaves Idle once it has something to play; the exact state
			// depends on whether the participants have reported themselves ready.
			await expect
				.poll(async () => {
					const groups = (await (
						await web.get("/SyncPlay/List")
					).json()) as Group[];
					return groups.find((g) => g.GroupId === group.GroupId)?.State;
				})
				.not.toBe("Idle");
		} finally {
			await guest.post("/SyncPlay/Leave");
			await web.post("/SyncPlay/Leave");
		}
	});
});
