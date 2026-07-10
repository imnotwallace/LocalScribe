# Mute controls, mute-state awareness & echo dedup (design)

- **Status:** Design (2026-07-10). Driven by a real Webex smoke-test finding: the user muted inside
  Webex, but LocalScribe kept recording their voice — and the same speech appeared on both legs
  ("Me" + "Them") without the echo copy being dedup-hidden.
- **Research basis:** adversarially-verified research run 2026-07-10 (7 agents incl. an empirical
  WASAPI probe executed on this machine, re-run independently by a verifier). Key verified facts:
  1. **Webex/Teams in-app mute is invisible to the OS.** Both apps keep their mic capture stream
     open and reading while "muted" (PETS 2022, *Are You Really Muted?*, arXiv:2204.06128 — verified
     against the paper's PDF; probe confirmed CiscoCollabHost's capture session live on this box).
  2. **Per-app capture-session mute does not exist as an independent state** — on capture endpoints,
     session mute is tied bidirectionally to the *device* (endpoint) mute (probe-verified; MS docs
     note the tie and warn not to rely on it). So there is no WASAPI flag Webex could even set.
  3. **Teams' local WebSocket API (port 8124) is retired** — Microsoft MC1266901 killed it
     permanently on 2026-06-30, no replacement. Off the table.
  4. **Webex has no desktop-local API of any kind** (xAPI is hardware-only; Embedded Apps expose no
     mute). The only proven direct read is **UI Automation scraping** of the meeting controls —
     what MuteDeck sells, with documented fragility (English UI, controls visible, breaks on updates).
  5. **Device/endpoint-level mute IS cleanly observable** (IAudioEndpointVolume mute + change
     callback), including a HID-synced headset mute where Webex is configured to sync with it.
- **User-locked decisions (2026-07-10):** build ALL THREE mute mechanisms (LocalScribe's own mute
  button + device-mute detection + advisory UIA detection), mute semantics = **stop capturing the
  local leg while muted** (privilege protection, one-sided Pause), and extend the echo dedup
  (bidirectional + containment-aware).
- **Companions:** `docs/specs/localscribe-specs.md` §2.1 (lifecycle), §5 (phantom bleed), §8
  (markers/indicators); `docs/plans/2026-07-08-live-recording-latency-and-stop-fixes-plan.md`
  (the Stop/Start machinery this builds on).

## 0. Framing

LocalScribe records the raw microphone **by design** — that is the only way to get a clean local
leg. No conferencing app propagates its soft-mute to the OS, so *"follow the app's mute"* cannot be
made reliable. The dependable inversion: give the user the mute control **in LocalScribe**, make
device-level mute (which IS observable) visible, and offer the fragile app-mute signal as advisory
awareness only.

## 1. Feature A — "Mute my side" control (the reliable core)

**Semantics (user-locked): muted = the local leg is not captured at all.** Privileged asides never
enter the evidentiary record. This extends the existing Pause precedent ("Pause STOPS capture —
privilege protection", SessionController doc) to one leg.

- **Core — `SessionController.SetLocalMuteAsync(bool muted, CancellationToken)`** (serialized on
  `_gate` like every lifecycle method):
  - Valid while `Recording` or `Paused`; no-op with a Notice otherwise.
  - **Mute while Recording:** `await s.Local.StopLegAndFlushAsync()` (same clean flush Pause uses —
    the VAD trailing utterance is kept), write marker `Markers.LocalMuted` (`"microphone muted by
    user"`) at `clock.ElapsedMs`, set `s.LocalMuted = true`.
  - **Unmute while Recording:** fresh local leg exactly like Resume's local half —
    `_captureProvider.CreateMic(s.Clock)` + `s.Local.StartLeg(mic, s.CaptureCts.Token,
    s.FeedCts.Token)` — honoring the same `FellBackToDefault` marker/notice path Resume has; write
    marker `Markers.LocalUnmuted` (`"microphone unmuted"`); reseed the local `SilentLegMonitor`
    (fresh leg ⇒ fresh grace window, mirroring Resume) and raise `SilentLegCleared(Local)` if it was
    flagged; abandon `_localStartPeak` (start-probe is Start-only, same rule as Resume).
  - **While Paused:** just flip `s.LocalMuted` + write the marker — no legs run while paused.
  - **Resume honors mute (privilege-safe):** `ResumeAsync` restarts the local leg **only when
    `!s.LocalMuted`**. Resuming must never silently unmute — a user who muted for a privileged aside
    and then paused would otherwise leak audio on resume. Remote always restarts.
  - **Stop while muted:** already handled — `StopLegAndFlushAsync` no-ops on a stopped leg
    (`_legSource is null`), `PadToMs` pads the local file to `durationMs`. The muted span is silence
    in `local.flac`, bracketed by markers; timeline alignment is preserved by the existing
    `AlignedAudioWriter` gap-fill (the first post-unmute frame is stamped on the session clock, so
    the gap back-fills as silence automatically).
  - **State exposure:** `bool LocalMuted` property + `LocalMuteChanged(bool)` event (dispatch
    contract identical to `SilentLegDetected`).
- **New markers** (`Model/Markers.cs`): `LocalMuted = "microphone muted by user"`,
  `LocalUnmuted = "microphone unmuted"`. Evidentiary: the record shows exactly when and that it was
  user-initiated.
- **App:** a `Mute my side` / `Unmute` toggle button on the Record console's recording panel
  (`LiveViewWindow.xaml`, next to Pause/Stop), bound via `SessionViewModel` (`IsLocalMuted` +
  `MuteLocalCommand`, marshalled like the silent-leg flags). While muted the console shows a
  persistent state line ("Your side is muted — not being recorded"), visually distinct from the
  *warning*-style banners (this one is user-intended state, not a problem).
- **No global hotkey** (locked decision from Stage 4: global hotkeys permanently dropped).

**Tests (Core):** mute-then-unmute writes both markers at the right clock times and produces a
fresh mic leg (`MicCreates` increments); frames captured before mute and after unmute land in the
audio file with the muted gap as silence (WAV-assertable like the pad tests); resume-honors-mute
(mute → pause → resume ⇒ local leg NOT restarted, remote restarted; unmute after resume restarts
it); mute while Paused flips state + marker only; Stop while muted finalizes cleanly with padded
audio. **App:** VM command/state round-trip, marshalled events.

## 2. Feature B — device-level (endpoint) mute awareness

The one mute that IS reliably observable — and it also silences LocalScribe's own capture, so the
user must know immediately (today they'd wait 15 s for the silent-leg heuristic).

- **Core — retain the endpoint and watch it.** `MicCaptureSource` currently discards its `MMDevice`
  after reading Id/Name (`MicCaptureSource.cs:43`); keep the reference, subscribe
  `device.AudioEndpointVolume.OnVolumeNotification`, and expose
  `event Action<bool> DeviceMuteChanged` + `bool DeviceMuted` (initial read at `Start()`).
  NAudio delivers notifications on an arbitrary thread — the source just raises; consumers marshal.
  Dispose unsubscribes (the existing dispose already owns the capture; add the endpoint handle).
- **`SessionController`:** subscribes per local leg (Start/Resume/unmute create fresh sources);
  on change while `Recording`: write marker (`Markers.MicDeviceMuted = "microphone device muted"` /
  `MicDeviceUnmuted = "microphone device unmuted"`) + raise `MicDeviceMuteChanged(bool)` for the
  console. Initial state at leg start: if already muted, marker + event immediately (don't wait for
  a change).
- **App:** warning-style banner in the console: "Your microphone device is muted — nothing is being
  recorded from it." (This complements, not replaces, the silent-leg monitor: the monitor catches
  wrong-device/noise-floor; this catches the explicit device mute instantly.)
- **Interaction with Feature A:** independent signals — device mute shows the warning banner even
  while app-level unmuted; LocalScribe-mute suppresses the banner (leg not running ⇒ no
  notifications matter; also nothing to warn about — the user muted deliberately).

**Tests (Core):** fake capture source gains a settable `DeviceMuted` + event raise; controller
writes the marker + raises the event on change and on already-muted-at-start; no marker while
`LocalMuted`. (The real NAudio endpoint-volume path is smoke-only, like device hot-unplug.)

## 3. Feature C — advisory app-mute detection (UIA watcher)

**Advisory only, by design.** The signal is scraped from the meeting app's UI (the MuteDeck
approach — the only mechanism that survives 2026: Teams' local API is retired, Webex never had one).
It is best-effort and version-fragile, so:

- **It never gates recording** — it only informs.
- **It writes NO transcript markers.** An evidentiary record must not contain claims derived from a
  signal that "may work intermittently" (MuteDeck's own wording for Webex). Banner only. (If the
  spike proves the signal is rock-solid for a given app version, markers can be revisited later.)
- **Banner copy makes the semantics unmissable:** "Webex looks muted — note that LocalScribe still
  records your microphone (use *Mute my side* to stop)." — i.e. the banner's job is to teach
  exactly the mental-model gap this design exists to fix, at the moment it matters.

**Architecture:**
- `IAppMuteWatcher` (Core seam, WPF-free): `Start(uint pid, string processName)`, `Stop()`,
  `event Action<AppMuteState>` where `AppMuteState ∈ {Muted, Unmuted, Unknown}`. `Unknown` is the
  dominant honest state (no meeting window, unsupported app, scrape failed) and clears the banner.
- **Implementation lives in the App layer** (`LocalScribe.App/Services/UiaAppMuteWatcher.cs`) —
  UIA is inherently a UI concern; Core consumes the seam only. Per-app adapters (Webex first,
  Teams second) locate the meeting-controls toolbar by ControlType/AutomationId descent (language-
  independent where possible), read the mic toggle's state, poll ~1 s + UIA property-change events
  where available. Read-only: never invoke patterns, never focus windows. Every failure path →
  `Unknown`, never throws outward.
- **Wiring:** the Record console owns the watcher lifecycle (start on session start with the
  planner-resolved remote PID — `RemoteCapturePlanner` already resolves it; stop on Idle). The
  banner binds to the watcher state. Core is untouched except for nothing — this feature can be
  entirely App-side. (Decision: keep it App-side; no Core changes.)
- **SPIKE REQUIRED before the detailed plan:** the current Webex (CiscoCollabHost) and new-Teams
  UIA trees must be probed on this box (Accessibility-Insights-style dump of the meeting-controls
  toolbar during a REAL call — needs the user to have a Webex/Teams meeting open). The spike
  deliverable is a probe tool + a runbook step for the user + a findings note that fixes the
  adapter selectors. **The watcher's implementation plan is written only after the spike.**
  Known risks the spike must answer: does CEF/Electron populate the UIA tree without a
  screen-reader flag; are AutomationIds stable and language-independent; is the toolbar present
  when meeting controls auto-hide.

## 4. Feature D — echo dedup extension (bidirectional + containment)

**Observed misses (measured against the implemented metric):** all four screenshot pairs scored
0.18–0.67 vs the 0.85 similarity bar, and the dedup only hides **Local** phantoms of Remote
originals (`PhantomBleedDedup.cs:34`) — the session's echo ran the *opposite* direction (the user's
voice rendered back on the Remote leg).

**Changes:**
1. **Containment similarity.** Add to `TextDistance`: `ContainmentSimilarity(a, b)` — normalized
   similarity of the shorter text against its best-aligned window of the longer (sliding
   char-window Levenshtein or token-level best alignment). Pair 1 ("So I'm gonna be testing sound."
   ⊂ "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.") scores ~1.0 on containment
   vs 0.50 whole-string. The dedup gate becomes `max(NormalizedSimilarity, ContainmentSimilarity)`
   **with a minimum-length guard on the shorter text** (normalized length ≥ 12 chars AND ≥ 3
   tokens) so "yeah"/"okay" can never containment-match everything.
2. **Bidirectional hiding.** `PhantomBleedDedup` gains the Remote-phantom-of-Local direction: hide
   a Remote segment that matches a near-simultaneous Local segment. Safety differences from the
   Local direction (a genuine remote speaker echoing your words must survive):
   - the same `NearWindowMs = 750` window;
   - the containment/similarity gate as above;
   - **RMS evidence required** for the Remote direction (no text-only fallback): live segments
     always carry RmsDb (`TranscriptMerger.cs:30`). Which side is louder is NOT assumed (an echo
     through the far end can come back louder); instead the Remote-direction hide requires
     `similarity ≥ MinSimilarity` AND `|localRms − remoteRms| ≥ MinRmsGapDb` — i.e. the two copies
     are energetically distinct (a same-room genuine conversation tends to be comparable; an echo
     path shifts energy). This is the most conservative defensible gate without a golden corpus.
   - the human-corrected / split exemption applies to the Remote direction identically.
3. **Honest expectations (documented in code + spec):** heavily garbled echo pairs (pair 3: "hold
   on to my name" vs "Hold on my mind", 0.67) remain visible — no safe text gate catches them. The
   dedup mitigates high-fidelity echoes; it does not promise echo-free views. Real calls with the
   far side on headphones rarely produce this at all (the smoke-test echo came from a second device
   in the room).
4. **Tuning constraint respected:** `PhantomBleedOptions`'s "tune ONLY against the golden corpus"
   comment stays; this change adds *mechanism* (containment metric, second direction) with the
   existing threshold VALUES unchanged, plus unit tests pinning the four observed pairs (pair 1
   hidden; pairs 2/3 kept — with the reason in the test name) and the classic direction's existing
   tests untouched.
5. **Render-only, as today:** JSONL keeps both copies; only the shared projection hides
   (`SessionProjectionLoader` → `TranscriptProjection` step 4 — one pipeline for read view, .md,
   .txt, .docx). **Known product gap carried forward (pre-existing):** a wrongly-hidden segment has
   no un-hide UI (hidden rows never reach the read view). Out of scope here; noted for the
   transcript editor's Edit mode as a future "show hidden" toggle.

**Tests (Core):** containment metric unit tests (perfect prefix containment ≈ 1.0; short-text guard
refuses; garbled pair stays < bar); bidirectional dedup — pair 1 hidden under RMS gap, kept without
RMS gap; genuine remote repetition (similar text, comparable RMS) kept; existing Local-direction
suite byte-identical; corrected/split exemption in the new direction.

## 5. Delivery & sequencing

One branch (`mute-controls-echo-dedup`), subagent-driven TDD, per-task + whole-branch review:
- **Phase A** — Feature A (Core mute + markers + console button). Highest user value, zero research risk.
- **Phase B** — Feature B (endpoint-mute awareness). Small, reliable.
- **Phase C** — Feature D (echo dedup). Pure/unit-testable.
- **Phase D** — Feature C spike (UIA probe tool + user runbook during a real Webex call), THEN the
  watcher plan+implementation from the spike findings.
Phases A–C are plannable now; Phase D's implementation plan is gated on the spike.

## 6. Non-goals

- No "follow the app's mute" recording gate — impossible to do reliably (research above); the UIA
  signal is advisory-only and writes no markers.
- No MuteDeck integration (works only if the user buys/installs MuteDeck; revisit on demand).
- No global mute hotkey (Stage-4 locked decision stands).
- No un-hide UI for dedup-hidden segments (pre-existing gap; noted for the transcript editor).
- No change to dedup threshold VALUES (golden-corpus-gated); mechanism only.

## 7. Spec deltas (`docs/specs/localscribe-specs.md`)

- §2.1: local-leg mute state in the lifecycle (mute stops local capture; Resume honors mute;
  markers `microphone muted by user`/`microphone unmuted`; device-mute markers).
- §5: phantom-bleed dedup is bidirectional with a containment metric; Remote-direction hide
  requires RMS evidence; threshold values unchanged.
- §8: the two new marker pairs + the device-mute and app-mute-advisory console indicators.
