import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import type { Page } from "@playwright/test";
import { JELLYFIN_URL } from "../playwright.config";

/**
 * Runs the real jellyfin-chromecast bundle in the test browser.
 *
 * The receiver is served from Jellyfin's own origin (by request interception,
 * not by the server) so that its fetches to the server are same-origin, and the
 * Cast Application Framework it expects is replaced by e2e/receiver/caf-stub.js.
 * What that buys is the receiver's own JavaScript: message dispatch, item
 * lookup, bitrate handling, device profile, PlaybackInfo, createStreamInfo and
 * the url it hands to the player. What it cannot buy is the device: there is no
 * media pipeline, no cast protocol and no real sender behind the stub.
 */

declare global {
	interface Window {
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		__castHarness: any;
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		__castSend: (data: any) => void;
	}
}

const RECEIVER_ROOT = "/__receiver__";

const MIME: Record<string, string> = {
	".css": "text/css",
	".html": "text/html",
	".ico": "image/x-icon",
	".js": "text/javascript",
	".png": "image/png",
	".svg": "image/svg+xml",
};

export function receiverDist(): string {
	const dir =
		process.env.RECEIVER_DIR ??
		path.join(__dirname, "..", ".artifacts", "receiver");

	return path.join(dir, "dist");
}

export function buildReceiver(): void {
	execFileSync(path.join(__dirname, "..", "receiver", "build.sh"), [], {
		stdio: "inherit",
		env: process.env,
	});
}

export type CastItem = {
	Id: string;
	Name?: string;
	Type?: string;
	MediaType?: string;
	IsFolder?: boolean;
};

export type CastMessage = {
	command: string;
	accessToken: string;
	serverAddress: string;
	receiverName?: string;
	maxBitrate?: number;
	options: {
		items: CastItem[];
		startPositionTicks?: number;
	};
};

export type ReceiverRun = {
	console: Array<{ level: string; text: string }>;
	errors: Array<{ kind: string; message: string; stack: string | null }>;
	loads: Array<Record<string, any>>;
	mediaInformation: Array<Record<string, any>>;
	busMessages: Array<{ namespace: string; senderId: string; message: any }>;
	started: { disableIdleTimeout: boolean } | null;
	requests: string[];
	/** The bodies the receiver posted to PlaybackInfo, device profile included. */
	playbackInfo: string[];
};

export type Recorder = { requests: string[]; playbackInfo: string[] };

export async function startReceiver(page: Page): Promise<Recorder> {
	const dist = receiverDist();

	if (!fs.existsSync(path.join(dist, "index.html"))) {
		throw new Error(
			`no receiver bundle in ${dist} — run e2e/receiver/build.sh first`,
		);
	}

	const recorder: Recorder = { playbackInfo: [], requests: [] };

	page.on("request", (request) => {
		if (request.url().startsWith(`${JELLYFIN_URL}${RECEIVER_ROOT}`)) {
			return;
		}

		recorder.requests.push(`${request.method()} ${request.url()}`);

		if (request.url().includes("/PlaybackInfo")) {
			recorder.playbackInfo.push(request.postData() ?? "");
		}
	});

	// The receiver loads the CAF SDK protocol-relative from gstatic; the stub
	// takes its place so nothing in this run depends on the network.
	await page.route(/gstatic\.com\/cast\/sdk/, (route) =>
		route.fulfill({
			body: fs.readFileSync(
				path.join(__dirname, "..", "receiver", "caf-stub.js"),
			),
			contentType: "text/javascript",
		}),
	);
	await page.route(/fonts\.googleapis\.com/, (route) =>
		route.fulfill({ body: "", contentType: "text/css" }),
	);

	await page.route(`${JELLYFIN_URL}${RECEIVER_ROOT}/**`, (route) => {
		const file = path.join(
			dist,
			new URL(route.request().url()).pathname.slice(RECEIVER_ROOT.length + 1),
		);

		if (!fs.existsSync(file)) {
			return route.fulfill({ status: 404, body: "" });
		}

		return route.fulfill({
			body: fs.readFileSync(file),
			contentType: MIME[path.extname(file)] ?? "application/octet-stream",
		});
	});

	await page.goto(`${JELLYFIN_URL}${RECEIVER_ROOT}/index.html`);
	await page.waitForFunction(
		() => window.__castHarness?.started !== null,
		null,
		{
			timeout: 15_000,
		},
	);

	return recorder;
}

/**
 * Hands the receiver the message a sender delivers for "play this item" and
 * waits until it either loads something into its player, reports an error to
 * the sender, or throws.
 */
export async function castAndWait(
	page: Page,
	message: CastMessage,
	recorder: Recorder,
	timeoutMs = 60_000,
): Promise<ReceiverRun> {
	await page.evaluate((data) => window.__castSend(data), message);

	const deadline = Date.now() + timeoutMs;

	while (Date.now() < deadline) {
		const done = await page.evaluate(() => {
			const harness = window.__castHarness;

			return (
				harness.loads.length > 0 ||
				harness.errors.length > 0 ||
				harness.busMessages.some((m: any) =>
					["error", "playbackerror", "connectionerror"].includes(
						m.message?.type,
					),
				)
			);
		});

		if (done) {
			break;
		}

		await page.waitForTimeout(500);
	}

	// Anything the receiver started after the load still belongs to this run.
	await page.waitForTimeout(1500);

	const harness = await page.evaluate(() => {
		const h = window.__castHarness;

		return {
			busMessages: h.busMessages,
			console: h.console,
			errors: h.errors,
			loads: h.loads,
			mediaInformation: h.mediaInformation,
			started: h.started,
		};
	});

	return {
		...harness,
		playbackInfo: [...recorder.playbackInfo],
		requests: [...recorder.requests],
	} as ReceiverRun;
}

/** How far the receiver got, in the order its own code reaches these. */
export function milestones(run: ReceiverRun): Record<string, boolean> {
	return {
		getItem: run.requests.some((r) => /GET .*\/Items\/[^/?]+(\?|$)/.test(r)),
		getMaxBitrate: run.console.some((c) => c.text.includes("getMaxBitrate")),
		getPlaybackInfo: run.requests.some((r) => r.includes("/PlaybackInfo")),
		createStreamInfo: run.console.some((c) =>
			c.text.includes("setting src to"),
		),
		playerLoad: run.loads.length > 0,
	};
}

export function streamUrl(run: ReceiverRun): string | null {
	return (run.loads[0]?.media?.contentId as string) ?? null;
}

export function playMethod(run: ReceiverRun): string | null {
	return (run.loads[0]?.media?.customData?.playMethod as string) ?? null;
}
