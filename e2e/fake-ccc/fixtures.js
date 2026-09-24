// Deterministic stand-in for the media.ccc.de dataset used by the e2e tests.
// Recording URLs point at /cdn/... which 302-redirects to /mirror/..., mirroring
// how the real CDN hands clients off to a mirror.

// Must match the fixture videos built by scripts/build-artifacts.sh; the Android
// suite raises it because driving a real player through the UI needs more than
// five seconds of material.
const SECONDS = Number(process.env.FIXTURE_SECONDS || 5);

// Jellyfin keeps a resume position only for items longer than five minutes
// (MinResumeDurationSeconds), so the resume tests need a talk of their own that
// clears that threshold. Must match FIXTURE_LONG_SECONDS in build-artifacts.sh.
const LONG_SECONDS = Number(process.env.FIXTURE_LONG_SECONDS || 330);

const CONFERENCE = {
	acronym: "e2e-congress",
	title: "E2E Congress 2025",
	slug: "congress/2025",
	description: "Fixture conference for the end-to-end tests",
	logo_url: "http://fake-ccc:3000/static/logo.png",
	url: "http://fake-ccc:3000/public/conferences/e2e-congress",
	event_last_released_at: "2025-12-30T12:00:00Z",
};

function recording(
	folder,
	mimeType,
	file,
	{
		highQuality = true,
		language = "deu",
		width = 1920,
		seconds = SECONDS,
	} = {},
) {
	return {
		size: 1,
		length: seconds,
		mime_type: mimeType,
		language,
		filename: file,
		state: "released",
		folder,
		high_quality: highQuality,
		width,
		height: Math.round((width * 9) / 16),
		recording_url: `http://fake-ccc:3000/cdn/${file}`,
		url: `http://fake-ccc:3000/public/recordings/${file}`,
		updated_at: "2025-12-30T12:00:00Z",
	};
}

// media.ccc.de publishes subtitles as recordings of their own: no size, no
// length, no dimensions, an empty folder, and a language that is the subtitle's
// own rather than the video's.
function subtitle(mimeType, file, { language = "eng", state = "complete" } = {}) {
	return {
		size: null,
		length: null,
		mime_type: mimeType,
		language,
		filename: file,
		state,
		folder: "",
		high_quality: true,
		width: null,
		height: null,
		recording_url: `http://fake-ccc:3000/cdn/${file}`,
		url: `http://fake-ccc:3000/public/recordings/${file}`,
		updated_at: "2025-12-30T12:00:00Z",
	};
}

const EVENTS = [
	{
		guid: "e2e-0000-0000-0000-000000000001",
		title: "Three stream talk",
		subtitle: "Video, audio description and audio",
		slug: "three-stream-talk",
		description:
			"A talk whose MP4 carries an extra video stream for the visually impaired.",
		original_language: "deu",
		persons: ["Alice Hacker", "Bob Builder"],
		tags: ["Security", "2025", "Stage HUFF"],
		view_count: 4200,
		date: "2025-12-27T11:00:00Z",
		release_date: "2025-12-28T11:00:00Z",
		duration: SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		poster_url: "http://fake-ccc:3000/static/poster.png",
		frontend_link: "http://fake-ccc:3000/v/three-stream-talk",
		conference_title: CONFERENCE.title,
		recordings: [
			recording("h264-hd", "video/mp4", "three-stream.mp4"),
			recording("webm-hd", "video/webm", "three-stream.webm"),
			recording("h264-sd", "video/mp4", "two-stream.mp4", {
				highQuality: false,
				width: 1024,
			}),
			// media.ccc.de publishes a translation as a recording of its own, so
			// picking another language means picking another version (#69).
			recording("h264-hd-translated", "video/mp4", "two-stream.mp4", {
				language: "eng",
			}),
			subtitle("text/vtt", "captions.eng.vtt"),
			subtitle("application/x-subrip", "captions.fin.srt", {
				language: "fin",
			}),
			// The API lists a subtitle as soon as a talk is queued for one; the
			// file only exists once the state leaves "todo".
			subtitle("text/vtt", "captions.deu.vtt", {
				language: "deu",
				state: "todo",
			}),
			// A filename that is only an extension is a leftover of the same
			// pipeline and 404s.
			subtitle("application/x-subrip", ".spa.srt", { language: "spa" }),
		],
		related: [{ event_guid: "e2e-0000-0000-0000-000000000002", weight: 9 }],
	},
	{
		guid: "e2e-0000-0000-0000-000000000002",
		title: "Two stream talk",
		slug: "two-stream-talk",
		description: "A plain video plus audio recording.",
		original_language: "eng",
		persons: ["Carol Coder"],
		tags: ["Ethics"],
		view_count: 150,
		date: "2025-12-28T15:00:00Z",
		release_date: "2025-12-29T15:00:00Z",
		duration: SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		conference_title: CONFERENCE.title,
		recordings: [
			recording("h264-hd", "video/mp4", "two-stream.mp4", { language: "eng" }),
			recording("webm-hd", "video/webm", "two-stream.webm", {
				language: "eng",
			}),
		],
		related: [],
	},
	{
		guid: "e2e-0000-0000-0000-000000000003",
		title: "Long talk",
		slug: "long-talk",
		description:
			"A talk long enough for Jellyfin to keep a resume position for it.",
		original_language: "eng",
		persons: ["Dave Debugger"],
		tags: ["Science"],
		// Below the 100 views the Recommended folder asks for, so this talk does
		// not reshuffle the existing ordering assertions.
		view_count: 42,
		date: "2025-12-29T09:00:00Z",
		release_date: "2025-12-29T18:00:00Z",
		duration: LONG_SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		conference_title: CONFERENCE.title,
		// MP4 only: a WebM of this length costs far more to encode than the
		// resume tests get out of it, and they never decode the video.
		recordings: [
			recording("h264-hd", "video/mp4", "long-talk.mp4", {
				language: "eng",
				seconds: LONG_SECONDS,
			}),
		],
		related: [],
	},
];

// A second conference nothing in the suite ever browses. It exists so the sync can be
// caught actually walking a conference into the library: an item for its talk cannot
// come from a test clicking through the folders, because none of them go here (#8).
const ARCHIVE = {
	acronym: "e2e-archive",
	title: "E2E Archive 2019",
	slug: "conferences/archive/2019",
	description: "Fixture conference the tests never open",
	logo_url: "http://fake-ccc:3000/static/logo.png",
	url: "http://fake-ccc:3000/public/conferences/e2e-archive",
	event_last_released_at: "2019-06-01T12:00:00Z",
};

const ARCHIVE_EVENTS = [
	{
		guid: "e2e-0000-0000-0000-00000000000a",
		title: "Archived talk",
		subtitle: "",
		slug: "archived-talk",
		description: "A talk in a conference the tests never open.",
		original_language: "deu",
		persons: ["Carol Coder"],
		tags: ["Archive"],
		// Below the Recommended threshold on purpose, so this conference cannot
		// disturb what the other specs assert about that row.
		view_count: 7,
		date: "2019-05-30T11:00:00Z",
		release_date: "2019-06-01T11:00:00Z",
		duration: SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		poster_url: "http://fake-ccc:3000/static/poster.png",
		frontend_link: "http://fake-ccc:3000/v/archived-talk",
		conference_title: ARCHIVE.title,
		recordings: [recording("h264-hd", "video/mp4", "two-stream.mp4")],
		related: [],
	},
];

// The list endpoint omits per-event detail, exactly like the real API.
const listEvent = (event) => {
	const { recordings, related, ...rest } = event;
	return rest;
};

// streaming.media.ccc.de answers with an empty list outside a congress and with a
// conference on air during one. Both are served, under different prefixes, so a test can
// pick the state it needs instead of depending on the calendar (#71).
const LIVE_CONFERENCE = {
	conference: "E2E Congress 2025",
	slug: "e2e-congress",
	isCurrentlyStreaming: true,
	groups: [
		{
			group: "Live",
			rooms: [
				{
					slug: "hall-e2e",
					display: "Hall E2E",
					thumb: "http://fake-ccc:3000/static/thumb.png",
					link: "http://fake-ccc:3000/live/hall-e2e",
					talks: {
						current: { title: "Live opening", speaker: "Erika Emcee" },
						next: { title: "Live closing" },
					},
					streams: [
						{
							slug: "hd-native",
							display: "1920x1080",
							type: "video",
							isTranslated: false,
							videoSize: [1920, 1080],
							urls: {
								hls: {
									display: "1920x1080 HLS",
									tech: "hls",
									url: "http://fake-ccc:3000/cdn/live.m3u8",
								},
							},
						},
					],
				},
			],
		},
	],
};

const ALL_EVENTS = [...EVENTS, ...ARCHIVE_EVENTS];

module.exports = {
	CONFERENCE,
	liveStreams: (onAir) => (onAir ? [LIVE_CONFERENCE] : []),
	ARCHIVE,
	EVENTS,
	conferenceList: () => ({
		conferences: [CONFERENCE, ARCHIVE].map((c) => ({ ...c, events: undefined })),
	}),
	conferenceDetail: (acronym) =>
		acronym === ARCHIVE.acronym
			? { ...ARCHIVE, events: ARCHIVE_EVENTS.map(listEvent) }
			: { ...CONFERENCE, events: EVENTS.map(listEvent) },
	event: (guid) => ALL_EVENTS.find((e) => e.guid === guid),
	search: (query) => ({
		events: ALL_EVENTS.filter((e) =>
			e.title.toLowerCase().includes(String(query || "").toLowerCase()),
		).map(listEvent),
	}),
};
