/*
 * Stands in for //www.gstatic.com/cast/sdk/libs/caf_receiver/v3/cast_receiver_framework.js
 * so the real jellyfin-chromecast bundle can boot in an ordinary browser.
 *
 * It implements only the Cast Application Framework surface the receiver
 * touches between start-up and handing a url to its player. It is not a
 * Chromecast: there is no media pipeline behind playerManager.load(), no cast
 * protocol and no real sender, so nothing past "the receiver picked this url"
 * is reproduced here.
 */
(() => {
	const harness = {
		console: [],
		errors: [],
		loads: [],
		mediaInformation: [],
		busMessages: [],
		started: null,
		canDisplayType: [],
	};

	window.__castHarness = harness;

	for (const level of ["log", "info", "warn", "error", "debug"]) {
		const original = console[level].bind(console);

		console[level] = (...args) => {
			harness.console.push({
				level,
				text: args
					.map((a) => {
						if (typeof a === "string") {
							return a;
						}

						try {
							return JSON.stringify(a);
						} catch {
							return String(a);
						}
					})
					.join(" "),
			});
			original(...args);
		};
	}

	window.addEventListener("error", (event) => {
		harness.errors.push({
			kind: "error",
			message: String(event.message),
			stack: event.error?.stack ?? null,
		});
	});
	window.addEventListener("unhandledrejection", (event) => {
		harness.errors.push({
			kind: "unhandledrejection",
			message: String(event.reason?.message ?? event.reason),
			stack: event.reason?.stack ?? null,
		});
	});

	/*
	 * A Chromecast's answer, not this browser's: Playwright's Chromium has no
	 * H.264 or AAC, so asking it would deny direct play to every mp4 and make
	 * the direct-play control impossible to set up.
	 *
	 * The H.264 answer has to be a device's and not merely permissive.
	 * deviceprofileBuilder merges its codec conditions into one only while every
	 * supported profile shares a bit depth; claiming High 10 as well splits them
	 * into mutually exclusive `VideoProfile Equals` conditions, and Jellyfin
	 * applies all of them at once, so nothing direct plays. Real hardware
	 * answers no to High 10, which is what keeps the merged condition.
	 */
	const H264_PROFILE_FLAGS = ["4200", "4240", "4d00", "6400"];

	const canDisplayType = (mimeType, codec) => {
		const supported = ((mime, codecString) => {
			if (!codecString) {
				return /^(video|audio)\/(mp4|webm|mpeg)$/.test(mime ?? "");
			}

			if (/^avc1\./.test(codecString)) {
				return (
					mime === "video/mp4" &&
					H264_PROFILE_FLAGS.includes(codecString.slice(5, 9).toLowerCase())
				);
			}

			if (/^vp0?9/.test(codecString)) {
				return mime === "video/webm" && /^vp09\.00\./.test(codecString);
			}

			if (codecString === "vp8") {
				return mime === "video/webm";
			}

			return /^(mp4a\.40|mp3|opus|vorbis|ac-3|ec-3|flac|wav)/.test(codecString);
		})(mimeType, codec);

		harness.canDisplayType.push({ mimeType, codec, supported });

		return supported;
	};

	class EventTargetish {
		constructor() {
			this.listeners = new Map();
		}

		addEventListener(type, handler) {
			const key = String(type);

			if (!this.listeners.has(key)) {
				this.listeners.set(key, []);
			}

			this.listeners.get(key).push(handler);
		}

		removeEventListener(type, handler) {
			const handlers = this.listeners.get(String(type)) ?? [];
			const index = handlers.indexOf(handler);

			if (index >= 0) {
				handlers.splice(index, 1);
			}
		}

		dispatch(type, event) {
			for (const handler of this.listeners.get(String(type)) ?? []) {
				handler(event ?? { type });
			}
		}
	}

	class PlayerManager extends EventTargetish {
		constructor() {
			super();
			this.state = "IDLE";
			this.mediaInfo = null;
		}

		getPlayerState() {
			return this.state;
		}

		getCurrentTimeSec() {
			return 0;
		}

		getMediaInformation() {
			return this.mediaInfo;
		}

		setMediaInformation(mediaInfo) {
			this.mediaInfo = mediaInfo;
			harness.mediaInformation.push(JSON.parse(JSON.stringify(mediaInfo)));
		}

		load(loadRequestData) {
			harness.loads.push(JSON.parse(JSON.stringify(loadRequestData)));
			this.mediaInfo = loadRequestData.media;
			this.state = "BUFFERING";
		}

		play() {}
		pause() {}
		stop() {}
		seek() {}

		getTextTracksManager() {
			return {
				getTrackById: () => undefined,
				setActiveByIds: () => undefined,
				setTextTrackStyle: () => undefined,
			};
		}
	}

	class CastReceiverContext extends EventTargetish {
		constructor() {
			super();
			this.playerManager = new PlayerManager();
			this.customMessageListeners = new Map();
		}

		static getInstance() {
			CastReceiverContext.instance ??= new CastReceiverContext();

			return CastReceiverContext.instance;
		}

		getPlayerManager() {
			return this.playerManager;
		}

		addCustomMessageListener(namespace, listener) {
			this.customMessageListeners.set(namespace, listener);
		}

		sendCustomMessage(namespace, senderId, message) {
			harness.busMessages.push({ namespace, senderId, message });
		}

		setLoggerLevel() {}

		start(options) {
			harness.started = { disableIdleTimeout: !!options?.disableIdleTimeout };
		}

		getSenders() {
			return [{ id: "harness-sender" }];
		}

		canDisplayType(mimeType, codec) {
			return canDisplayType(mimeType, codec);
		}

		getDeviceCapabilities() {
			return { display_supported: true, is_hdr_supported: false };
		}
	}

	const enumOf = (...names) =>
		Object.fromEntries(names.map((name) => [name, name]));

	class Bag {
		constructor(...args) {
			this.args = args;
		}
	}

	class Track {
		constructor(trackId, type) {
			this.trackId = trackId;
			this.type = type;
		}
	}

	class Image {
		constructor(url) {
			this.url = url;
		}
	}

	window.cast = {
		framework: {
			CastReceiverContext,
			CastReceiverOptions: class {},
			LoggerLevel: enumOf(
				"DEBUG",
				"NONE",
				"VERBOSE",
				"INFO",
				"WARNING",
				"ERROR",
			),
			PlaybackConfig: class {},
			ShakaVariant: enumOf("DEBUG", "RELEASE"),
			events: {
				EventType: enumOf(
					"ABORT",
					"ENDED",
					"MEDIA_FINISHED",
					"PAUSE",
					"PLAY",
					"PLAYER_LOAD_COMPLETE",
					"PLAYING",
					"TIME_UPDATE",
					"ERROR",
				),
				category: enumOf("CORE", "DEBUG", "FINE", "REQUEST"),
			},
			messages: {
				Command: { ALL_BASIC_MEDIA: 12303 },
				GenericMediaMetadata: Bag,
				Image,
				LoadRequestData: class {},
				MediaInformation: class {},
				MovieMediaMetadata: Bag,
				MusicTrackMediaMetadata: Bag,
				PhotoMediaMetadata: Bag,
				PlayerState: enumOf("IDLE", "PLAYING", "PAUSED", "BUFFERING"),
				StreamType: enumOf("BUFFERED", "LIVE", "NONE"),
				TextTrackEdgeType: enumOf("NONE", "OUTLINE", "DROP_SHADOW"),
				TextTrackStyle: class {},
				TextTrackType: enumOf("SUBTITLES", "CAPTIONS"),
				Track,
				TrackType: enumOf("TEXT", "AUDIO", "VIDEO"),
				TvShowMediaMetadata: Bag,
			},
			system: {
				DeviceCapabilities: {
					DISPLAY_SUPPORTED: "display_supported",
					IS_HDR_SUPPORTED: "is_hdr_supported",
				},
				EventType: enumOf("SYSTEM_VOLUME_CHANGED", "READY", "SHUTDOWN"),
			},
			ui: {
				Controls: {
					getInstance: () => ({
						assignButton: () => undefined,
						clearDefaultSlotAssignments: () => undefined,
					}),
				},
				ControlsButton: enumOf(
					"CAPTIONS",
					"SEEK_BACKWARD_15",
					"SEEK_FORWARD_15",
				),
				ControlsSlot: enumOf(
					"SLOT_PRIMARY_1",
					"SLOT_PRIMARY_2",
					"SLOT_SECONDARY_1",
					"SLOT_SECONDARY_2",
				),
			},
		},
	};

	/** Delivers a sender's message the way the cast platform would. */
	window.__castSend = (data) => {
		const context = CastReceiverContext.getInstance();
		const listener = context.customMessageListeners.get(
			"urn:x-cast:com.connectsdk",
		);

		if (!listener) {
			throw new Error(
				"the receiver registered no listener on urn:x-cast:com.connectsdk",
			);
		}

		listener({ data, senderId: "harness-sender" });
	};

	customElements.define("cast-media-player", class extends HTMLElement {});
})();
