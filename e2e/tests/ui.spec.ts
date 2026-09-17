import { expect, test } from "@playwright/test";
import {
	apiContext,
	channelItems,
	chaosflixChannelId,
	gotoAuthenticated,
	loginUi,
	setPluginConfig,
	userId,
} from "../helpers/jellyfin";

// Runs last on purpose: the web client asks for the channel's latest media,
// which re-parents the talks away from the conference folder (#15) and would
// leave the other specs with empty folders.
test.describe("web UI", () => {
	test.fixme("channel and its talks are visible in the web UI", async ({ page }) => {
		const api = await apiContext();
		const channelId = await chaosflixChannelId(api);
		await loginUi(page);

		await gotoAuthenticated(page, `/web/#/details?id=${channelId}`);
		await expect(page.getByText("📅 Browse by Year")).toBeVisible();

		const byYear = (await channelItems(api)).find((i) =>
			i.Name.includes("Browse by Year"),
		)!;
		const years = await channelItems(api, byYear.Id);
		const conferences = await channelItems(api, years[0].Id);

		await gotoAuthenticated(page, `/web/#/details?id=${conferences[0].Id}`);
		await expect(page.getByText("Three stream talk")).toBeVisible();
		await expect(page.getByText("Two stream talk")).toBeVisible();
	});

	test.fixme("talk plays in the browser with audio, and seeking works", async ({
		page,
	}) => {
		const api = await apiContext();
		// Chromium in Playwright ships without H.264; this talk is played as WebM.
		// Both talks have their own probe cache entry, so switching the format here
		// does not disturb the MP4 test above (see #3).
		await setPluginConfig(api, { PreferredFormat: "WebM" });
		const talk = await talkNamed(api, "Two stream talk");

		const source = await playbackInfo(api, talk.Id);
		expect(source.Container).toBe("webm");
		expect(
			source.MediaStreams.some((s: { Type: string }) => s.Type === "Audio"),
		).toBe(true);

		await loginUi(page);
		await gotoAuthenticated(page, `/web/#/details?id=${talk.Id}`);
		await page.getByRole("button", { name: /^play/i }).first().click();

		const video = page.locator("video");
		await expect(video).toBeVisible({ timeout: 60_000 });
		await expect
			.poll(
				async () => video.evaluate((v: HTMLVideoElement) => v.currentTime),
				{ timeout: 60_000 },
			)
			.toBeGreaterThan(0.5);

		const hasAudio = await video.evaluate(
			(
				v: HTMLVideoElement & {
					mozHasAudio?: boolean;
					webkitAudioDecodedByteCount?: number;
				},
			) => v.mozHasAudio || Number(v.webkitAudioDecodedByteCount) > 0,
		);
		expect(hasAudio, "the player decoded no audio").toBeTruthy();

		await video.evaluate((v: HTMLVideoElement) => {
			v.currentTime = 3;
		});
		await expect
			.poll(async () => video.evaluate((v: HTMLVideoElement) => v.currentTime))
			.toBeGreaterThan(2.5);

		await setPluginConfig(api, { PreferredFormat: "Mp4" });
	});
});
