import { expect, test } from "@playwright/test";
import {
	apiContext,
	channelItems,
	chaosflixChannelId,
	CONFERENCE_TITLE,
	conferenceFolder,
	conferenceFolders,
	loginUi,
	runScheduledTask,
	userId,
	yearFolders,
} from "../helpers/jellyfin";

test.describe("browsing the channel", () => {
	test("root offers the three virtual folders", async () => {
		const api = await apiContext();

		const names = (await channelItems(api)).map((i) => i.Name);

		expect(names).toEqual(
			expect.arrayContaining([
				"🔥 Popular Talks",
				"⭐ Recommended",
				"📅 Browse by Year",
			]),
		);
	});

	test("year leads to conference leads to talks", async () => {
		const api = await apiContext();

		const byYear = (await channelItems(api)).find((i) =>
			i.Name.includes("Browse by Year"),
		)!;
		const years = await channelItems(api, byYear.Id);
		// Jellyfin sorts channel items by name, so the older year comes first.
		expect(years.map((y) => y.Name)).toEqual(["2019", "2025"]);

		const conferences = await channelItems(api, years.find((y) => y.Name === "2025")!.Id);
		expect(conferences.map((c) => c.Name)).toEqual([CONFERENCE_TITLE]);

		// Jellyfin applies its own sort order to channel items.
		const talks = await channelItems(api, conferences[0].Id);
		expect(talks.map((t) => t.Name).sort()).toEqual([
			"Long talk",
			"Three stream talk",
			"Two stream talk",
		]);
	});

	test("talk carries metadata from the CCC API", async () => {
		const api = await apiContext();
		const user = await userId(api);

		const talks = await channelItems(api, (await conferenceFolder(api)).Id);
		const talk = talks.find((t) => t.Name === "Three stream talk")!;

		const details = await (
			await api.get(`/Users/${user}/Items/${talk.Id}`)
		).json();

		expect(details.RunTimeTicks).toBe(50_000_000);
		expect(details.Overview).toContain("Alice Hacker, Bob Builder");
		expect(details.Overview).toContain("4,200 views");
		expect(details.People.map((p: { Name: string }) => p.Name)).toEqual([
			"Alice Hacker",
			"Bob Builder",
		]);
		expect(details.Genres).toContain("Security");
		// Numeric tags and stage names are filtered out.
		expect(details.Genres).not.toContain("2025");
		expect(details.Genres).not.toContain("Stage HUFF");
	});

	test("popular offers the talks of the recent conferences", async () => {
		const api = await apiContext();

		const popular = (await channelItems(api)).find((i) =>
			i.Name.includes("Popular"),
		)!;
		const talks = await channelItems(api, popular.Id);

		// Jellyfin re-sorts channel items by name before handing them out, so the
		// view-count ranking the plugin applies is not observable here; it is
		// asserted in ChaosflixChannelTests instead.
		expect(talks.map((t) => t.Name).sort()).toEqual([
			"Archived talk",
			"Long talk",
			"Three stream talk",
			"Two stream talk",
		]);
	});

	test("recommended ranks talks by views and recency", async () => {
		const api = await apiContext();

		const recommended = (await channelItems(api)).find((i) =>
			i.Name.includes("Recommended"),
		)!;
		const talks = await channelItems(api, recommended.Id);

		// Only these two clear the >100 view threshold; the ranking favours views
		// over age.
		expect(talks.map((t) => t.Name)).toEqual([
			"Three stream talk",
			"Two stream talk",
		]);
	});

	// Regression test for #15: every folder used to hand out the same item id,
	// so listing one folder moved the talks out of all the others.
	test("talks stay in every folder across a channel refresh", async () => {
		const api = await apiContext();
		const folders = await channelItems(api);
		const conference = await conferenceFolder(api);

		for (const folder of folders) {
			await channelItems(api, folder.Id);
		}
		await runScheduledTask(api, "RefreshInternetChannels");

		const everything = [
			"Archived talk",
			"Long talk",
			"Three stream talk",
			"Two stream talk",
		];
		for (const [folder, expected] of [
			// A conference folder holds only its own talks; Popular draws from
			// every conference, so it also carries the archived one.
			[conference, everything.filter((n) => n !== "Archived talk")],
			[folders.find((i) => i.Name.includes("Popular"))!, everything],
			// The long talk stays below the Recommended view threshold.
			[
				folders.find((i) => i.Name.includes("Recommended"))!,
				["Three stream talk", "Two stream talk"],
			],
		] as const) {
			const talks = await channelItems(api, folder.Id);
			expect(
				talks.map((t) => t.Name).sort(),
				`${folder.Name} lost its talks`,
			).toEqual([...expected].sort());
		}
	});

	// #54: "Recently Added in Chaosflix" is fed by
	// /Users/{id}/Items/Latest?ParentId=<channel> — measured on 10.11.7 and on
	// 12.1 — which is a library query and therefore sees every folder-scoped
	// copy of a talk. The copies outside the conference folders are filed a
	// century in the past, so the row's window holds the conference copies only.
	test("recently added lists each talk once", async () => {
		const api = await apiContext();
		const user = await userId(api);
		const channel = await chaosflixChannelId(api);
		const folders = await channelItems(api);
		const years = await yearFolders(api);
		const conferences = await conferenceFolders(api);
		const conferenceTalks = await channelItems(
			api,
			(await conferenceFolder(api)).Id,
		);

		for (const folder of [...folders, ...years, ...conferences]) {
			await channelItems(api, folder.Id);
		}
		await runScheduledTask(api, "RefreshInternetChannels");

		// A real library holds far more talks than the row shows; here the row is
		// asked for exactly as many entries as there are talks.
		const row = (await (
			await api.get(
				`/Users/${user}/Items/Latest?ParentId=${channel}&Limit=${conferenceTalks.length}`,
			)
		).json()) as Array<{ Id: string; Name: string }>;

		expect(
			row.map((i) => i.Id).sort(),
			"the recently added row shows a copy from another folder",
		).toEqual(conferenceTalks.map((t) => t.Id).sort());
	});

	// #54, the remaining half: the copies are still separate library items, so a
	// query over the whole library lists a talk once per folder it was browsed
	// in. The folder scoping cannot simply be dropped — with one id per talk both
	// 10.11.7 and 12.1 move the item to the folder listed last and leave the
	// others empty, which is #15 and is what the folder test above guards.
	test.fixme("a talk listed in several folders exists once in the library", async () => {
		const api = await apiContext();
		const user = await userId(api);
		const folders = await channelItems(api);
		const years = await yearFolders(api);
		const conferences = await conferenceFolders(api);

		for (const folder of [...folders, ...years, ...conferences]) {
			await channelItems(api, folder.Id);
		}
		await runScheduledTask(api, "RefreshInternetChannels");

		const items = (
			await (await api.get(`/Items?userId=${user}&recursive=true`)).json()
		).Items as Array<{ Id: string; Name: string }>;

		const talks = ["Long talk", "Three stream talk", "Two stream talk"];
		const names = items
			.map((i) => i.Name)
			.filter((name) => talks.includes(name))
			.sort();
		expect(names, "the same talk is stored more than once").toEqual([
			...new Set(names),
		]);
	});
});
