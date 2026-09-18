import { type APIRequestContext, type APIResponse, request } from "@playwright/test";
import { JELLYFIN_URL } from "../playwright.config";
import { readToken } from "./jellyfin";
import type { MediaSource } from "./profiles";

/**
 * A Chromecast fetches the stream itself, from wherever the sender told it the
 * server is — it never inherits the sender's session or its network position.
 * These helpers reproduce that: they pick the url the way jellyfin-chromecast's
 * createStreamInfo does, resolve it the way the receiver resolves it, and
 * retrieve it over a plain HTTP client that has no Jellyfin credentials.
 */

/** A request context with no Authorization header, like the receiver's. */
export async function castClient(): Promise<APIRequestContext> {
	return request.newContext();
}

export type CastStream = {
	url: string;
	/** DirectPlay | DirectStream | Transcode, as the receiver would report it. */
	playMethod: "DirectPlay" | "DirectStream" | "Transcode";
};

/**
 * Mirrors jellyfin-chromecast `createStreamInfo` (src/helpers.ts): direct play
 * takes MediaSourceInfo.Path verbatim, direct stream builds a /Videos url, and
 * everything else follows TranscodingUrl.
 */
export function castStreamUrl(
	source: MediaSource,
	itemId: string,
	serverAddress: string = JELLYFIN_URL,
): CastStream {
	const token = readToken();

	if (source.SupportsDirectPlay && source.Path) {
		return { url: source.Path, playMethod: "DirectPlay" };
	}

	if (source.SupportsDirectStream) {
		return {
			url:
				`${serverAddress}/Videos/${itemId}/stream.${source.Container}` +
				`?mediaSourceId=${source.Id}&api_key=${token}&static=true`,
			playMethod: "DirectStream",
		};
	}

	if (!source.TranscodingUrl) {
		throw new Error(
			"the server offered neither direct play, direct stream nor a transcoding url",
		);
	}

	return {
		url: new URL(source.TranscodingUrl, serverAddress).toString(),
		playMethod: "Transcode",
	};
}

export type CastFetch = {
	/** The url that finally carried media bytes. */
	url: string;
	status: number;
	contentType: string;
	bytes: number;
};

/**
 * Retrieves media bytes from what the cast device was handed. For an HLS url
 * that means walking master playlist → variant playlist → first segment, which
 * is where a stream the receiver cannot reach stops being a 200 and starts
 * being a failure.
 */
export async function fetchCastMedia(
	client: APIRequestContext,
	stream: CastStream,
): Promise<CastFetch> {
	const first = await fetchOrExplain(client, stream.url);

	if (!isPlaylist(first.contentType, stream.url)) {
		return first;
	}

	const variantUrl = firstEntry(first.body, stream.url, "variant playlist");
	const variant = await fetchOrExplain(client, variantUrl);
	const segmentUrl = firstEntry(variant.body, variantUrl, "media segment");

	return fetchOrExplain(client, segmentUrl);
}

function isPlaylist(contentType: string, url: string): boolean {
	return contentType.includes("mpegurl") || url.includes(".m3u8");
}

function firstEntry(playlist: string, base: string, what: string): string {
	const entry = playlist
		.split("\n")
		.map((line) => line.trim())
		.find((line) => line && !line.startsWith("#"));

	if (!entry) {
		throw new Error(`no ${what} in the playlist at ${base}:\n${playlist}`);
	}

	return new URL(entry, base).toString();
}

async function fetchOrExplain(
	client: APIRequestContext,
	url: string,
): Promise<CastFetch & { body: string }> {
	let response: APIResponse;
	try {
		response = await client.get(url, { timeout: 60_000 });
	} catch (error) {
		throw new Error(
			`a cast device could not reach ${url}: ${(error as Error).message}`,
		);
	}

	const body = await response.body();
	return {
		url,
		status: response.status(),
		contentType: response.headers()["content-type"] ?? "",
		bytes: body.length,
		body: body.toString("utf8"),
	};
}
