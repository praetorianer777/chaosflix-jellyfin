import { expect, test } from "@playwright/test";
import {
	apiContext,
	channelItems,
	chaosflixChannelId,
	CONFERENCE_TITLE,
	loginUi,
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

	// Browsing Popular or Recommended moves the talks out of the conference
	// folder (#15), so these stay disabled until that is fixed.
	test.fixme("popular talks are ordered by view count", async () => {
		const api = await apiContext();

		const popular = (await channelItems(api)).find((i) =>
			i.Name.includes("Popular"),
		)!;
		const talks = await channelItems(api, popular.Id);

		expect(talks.map((t) => t.Name)).toEqual([
			"Three stream talk",
			"Two stream talk",
		]);
	});

	test.fixme("recommended ranks talks by views and recency", async () => {
		const api = await apiContext();

		const recommended = (await channelItems(api)).find((i) =>
			i.Name.includes("Recommended"),
		)!;
		const talks = await channelItems(api, recommended.Id);

		// Both clear the >100 view threshold; the ranking favours views over age.
		expect(talks.map((t) => t.Name)).toEqual([
			"Three stream talk",
			"Two stream talk",
		]);
	});
});
