import { expect, test } from "@playwright/test";
import {
	apiContext,
	channelItems,
	chaosflixChannelId,
	CONFERENCE_TITLE,
	loginUi,
	runScheduledTask,
	userId,
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
		expect(years.map((y) => y.Name)).toEqual(["2025"]);

		const conferences = await channelItems(api, years[0].Id);
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

		const byYear = (await channelItems(api)).find((i) =>
			i.Name.includes("Browse by Year"),
		)!;
		const years = await channelItems(api, byYear.Id);
		const conferences = await channelItems(api, years[0].Id);
		const talks = await channelItems(api, conferences[0].Id);
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
		const byYear = folders.find((i) => i.Name.includes("Browse by Year"))!;
		const years = await channelItems(api, byYear.Id);
		const conference = (await channelItems(api, years[0].Id))[0];

		for (const folder of folders) {
			await channelItems(api, folder.Id);
		}
		await runScheduledTask(api, "RefreshInternetChannels");

		const everything = ["Long talk", "Three stream talk", "Two stream talk"];
		for (const [folder, expected] of [
			[conference, everything],
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
});
