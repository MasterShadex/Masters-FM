using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// POST /webhook handler -- sub-stage 4.4 + 4.9e + 4.10.
/// Ports server.js handleWebhook (lines 903-1071) with all 11 behavioral requirements.
/// Concurrency safety: SemaphoreSlim(1,1) in ServerState serializes state mutations.
/// Art/duration resolution: full cascade wired in sub-stage 4.9e.
/// enrichByTitle (server.js:851-860): runs BEFORE WebhookLock when artist is placeholder.
/// Discord RPC: PushDiscord called at three sites (early, post-cascade, always-fires)
/// and in ArtRetryAsync (B11), mirroring server.js sseBroadcast() call pattern (4.10).
/// </summary>
internal static class WebhookHandler
{
    // ── Response bytes ────────────────────────────────────────────────────────
    private static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK");
    private static readonly byte[] s_badRequestBytes = Encoding.UTF8.GetBytes("Bad Request");

    // ── Phase M #1A — browser-source startedAt-resync cooldown ──────────────
    // YouTube ads insert ~30-second segments mid-video.  Chrome's SMTC reports
    // the AD'S timeline during the ad, so position jumps from 5:00 → 0:00 at
    // ad start and 0:30 → 5:30 at ad end.  Each looks like a real seek to our
    // bridge / heartbeat (jump > Phase H's 1000 ms threshold), so B7 and B10
    // fire twice in rapid succession and the OBS bar visibly snaps both ways.
    // The cooldown coalesces ad bursts (and any other Chrome-side jitter)
    // by refusing repeat resyncs within MinResyncIntervalMs.  Browser sources
    // only — desktop SMTC (Spotify, etc.) doesn't have this pattern.
    private const long MinResyncIntervalMs = 2000;

    private static bool IsBrowserLikeSource(string source) =>
        string.Equals(source, "youtube",      StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "youtubemusic", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "browser",      StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "twitch",       StringComparison.OrdinalIgnoreCase);

    // ── Route registration ────────────────────────────────────────────────────
    public static void MapWebhookEndpoint(WebApplication app)
    {
        // Matches server.js: req.method === 'POST' && req.url === '/webhook'
        // Response: 200 "OK" on success, 400 "Bad Request" on parse error.
        app.MapPost("/webhook", HandleAsync);
    }

    // ── Main handler ──────────────────────────────────────────────────────────
    private static async Task HandleAsync(
        HttpContext ctx,
        ServerState state,
        ArtCascade artCascade,
        DurationResolver durationResolver,
        DiscordRpcService discordRpcService,
        IHttpClientFactory httpClientFactory,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // --- Parse body (mirrors server.js JSON.parse in handleWebhook) ---
        string bodyText;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            bodyText = await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook body read error");
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(s_badRequestBytes, ct);
            return;
        }

        JsonNode? data;
        try
        {
            data = JsonNode.Parse(bodyText);
        }
        catch (JsonException)
        {
            // Mirrors server.js catch block: return false -> 400
            logger.LogWarning("Webhook parse error: invalid JSON body");
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(s_badRequestBytes, ct);
            return;
        }

        if (data == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(s_badRequestBytes, ct);
            return;
        }

        // --- Extract fields (server.js lines 907-926) ---
        var artist = (string?)data["artist"] ?? string.Empty;
        var track = (string?)data["track"] ?? string.Empty;

        // B2: Duration unit conversion (server.js line 909)
        // tray sends seconds (float); store as milliseconds (long).
        var durationMs = (long)Math.Round(((double?)data["duration"]?.GetValue<double>() ?? 0.0) * 1000.0);

        // positionMs: round to nearest ms (server.js line 910)
        var positionMs = data["positionMs"] != null
            ? (long)Math.Round(data["positionMs"]!.GetValue<double>())
            : 0L;

        var source = (string?)data["source"] ?? "Webhook";
        var isPaused = data["isPaused"]?.GetValue<bool>() == true;

        // server.js line 926: data.seek === true  (field name is "seek", not "isSeek")
        var isSeek = data["seek"]?.GetValue<bool>() == true;

        var webhookArt = (string?)data["trackArt"] ?? string.Empty;
        var originUrl = (string?)data["originUrl"] ?? string.Empty;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // --- enrichByTitle: recover artist from track title when SMTC gives placeholder ---
        // Mirrors server.js:851-860 (inside setTrack). Runs OUTSIDE WebhookLock so HTTP
        // calls don't hold the state lock. Enriched values flow into isSameTrack + newTrack.
        // See intentional differences ID-31: runs on every webhook where artist is placeholder.
        if (TextNormalization.IsPlaceholderArtist(artist))
        {
            var enriched = await TextNormalization.EnrichByTitleAsync(
                track, httpClientFactory, logger, ct);
            if (enriched.HasValue)
            {
                artist = enriched.Value.Artist;
                track  = enriched.Value.Track;
                if (!IsValidArt(webhookArt) && IsValidArt(enriched.Value.Art))
                    webhookArt = enriched.Value.Art;
            }
        }

        // --- Acquire webhook lock for state mutation ---
        await state.WebhookLock.WaitAsync(ct);

        bool needsNewTrackArtResolution = false;
        string resolveArtist = artist;
        string resolveTrack = track;
        string resolveWebhookArt = webhookArt;

        try
        {
            var prev = state.CurrentTrack;

            // B1: isSameTrack normalization (server.js lines 343-345, 919, 928)
            bool hadPrevTrack = prev != null;
            bool isNewTrack = !IsSameTrack(artist, track, prev);
            bool isTrackChange = hadPrevTrack && isNewTrack; // mid-session track change

            // B3 + B4: startedAt calculation with stale position guard (lines 921-922)
            // Guard: on mid-session track CHANGE, SMTC positionMs can be stale (old track).
            // NOT applied on first webhook after server boot (hadPrevTrack=false).
            long trustedPos = (isTrackChange && positionMs > 5000) ? 0L : positionMs;
            long startedAt = trustedPos > 0L ? (nowMs - trustedPos) : nowMs;

            bool same = !isNewTrack;

            // --- Timestamp diagnostic (server.js lines 933-939) ---
            if (same && prev != null && positionMs >= 1000)
            {
                long prevStartedAt = (long?)prev["startedAt"]?.GetValue<long>() ?? nowMs;
                long expectedMs = nowMs - prevStartedAt;
                long drift = expectedMs - positionMs;
                int diagCount = state.IncrementTsDiagCount();
                if (diagCount % 5 == 0)
                {
                    logger.LogInformation(
                        "[{Source}] reported={ReportedS}s overlay={OverlayS}s drift={Sign}{DriftS}s paused={Paused}",
                        source,
                        Math.Round(positionMs / 1000.0),
                        Math.Round(expectedMs / 1000.0),
                        drift > 0 ? "+" : string.Empty,
                        Math.Round(drift / 1000.0),
                        isPaused);
                }
            }

            // --- Webhook arrival logging (server.js lines 943-947) ---
            bool pauseFlipped = prev != null && same && (prev["isPaused"]?.GetValue<bool>() == true) != isPaused;
            bool durationNewlyKnown = prev != null && same && durationMs > 0
                && !((long?)prev["duration"]?.GetValue<long>() > 0);
            if (!same || pauseFlipped || durationNewlyKnown)
            {
                logger.LogInformation(
                    "Webhook [{Source}]: \"{Artist} - {Track}\" dur={DurS}s pos={PosS}s art={Art} paused={Paused} same={Same}",
                    source, artist, track,
                    Math.Round(durationMs / 1000.0),
                    Math.Round(positionMs / 1000.0),
                    string.IsNullOrEmpty(webhookArt) ? "no" : "yes",
                    isPaused, same);
            }

            if (!same)
            {
                // --- NEW TRACK: call SetTrackSynchronous (server.js setTrack, lines 843-901) ---
                // B3 + B4 already computed startedAt above.
                // artist/track are already enriched by enrichByTitle (if applicable).
                var resolvedArtist = artist.Trim();
                var startPaused = isPaused;
                var newTrack = new JsonObject
                {
                    ["artist"]    = JsonValue.Create(resolvedArtist),
                    ["track"]     = JsonValue.Create(track),
                    ["trackArt"]      = JsonValue.Create(string.Empty),  // art set after resolution
                    ["trackArtHttps"] = JsonValue.Create(string.Empty),  // HTTPS-only art for Discord RPC (data: URIs filtered out)
                    ["duration"]  = JsonValue.Create(durationMs),
                    ["originUrl"] = JsonValue.Create(originUrl),
                    ["startedAt"] = JsonValue.Create(startedAt),
                    ["source"]    = JsonValue.Create(source),
                    ["isPaused"]  = JsonValue.Create(startPaused),
                    ["pausedAt"]  = JsonValue.Create(startPaused ? nowMs : 0L),
                    ["_setAt"]    = JsonValue.Create(nowMs),
                };
                logger.LogInformation(
                    "{Source}: {Artist} - {Track}{Dur}{Pos}",
                    source, resolvedArtist, track,
                    durationMs > 0 ? $" ({Math.Round(durationMs / 1000.0)}s)" : string.Empty,
                    startedAt != nowMs ? $" pos={Math.Round((nowMs - startedAt) / 1000.0)}s" : string.Empty);

                state.CurrentTrack = newTrack;
                state.ArtResolved  = false;
                state.ArtResolving = true;
                state.ArtHttpsRetries = 0;   // v14 Phase 5: fresh HTTPS-retry budget per track
                // Phase M #1A: new track = fresh cooldown window.  First seek
                // / drift correction on this track always applies; subsequent
                // ones within MinResyncIntervalMs are suppressed.
                state.LastStartedAtUpdateMs = 0;

                // Signal that art + duration resolution runs AFTER releasing the lock
                needsNewTrackArtResolution = true;
                resolveArtist     = resolvedArtist;
                resolveTrack      = track;
                resolveWebhookArt = webhookArt;
            }
            else
            {
                // --- SAME TRACK: unified pause / resume / seek / drift handling ---
                // Work on a clone so we can set it back atomically via CurrentTrack setter.
                var ct2 = prev!.DeepClone(); // local mutable copy
                bool ct2Dirty = false;       // set true when ct2 is actually mutated

                // B5: Pause handling (server.js lines 961-974)
                bool wasPaused = ct2["isPaused"]?.GetValue<bool>() == true;
                if (isPaused)
                {
                    if (!wasPaused)
                    {
                        // Entering pause -- re-pin startedAt and set pausedAt
                        ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
                        ct2["pausedAt"]  = JsonValue.Create(nowMs);
                        ct2["isPaused"]  = JsonValue.Create(true);
                        ct2Dirty = true;
                        logger.LogInformation(
                            "Paused: {Artist} - {Track} @ {PosS}s (re-synced to source)",
                            artist, track, Math.Round(positionMs / 1000.0));
                    }
                    // else: already paused -- leave startedAt/pausedAt alone (server.js line 974)
                }
                else
                {
                    // B6: Resume handling (server.js lines 976-986)
                    if (wasPaused)
                    {
                        // Resuming: re-sync position and clear pause fields
                        ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
                        ct2["isPaused"]  = JsonValue.Create(false);
                        ct2["pausedAt"]  = JsonValue.Create(0L);
                        ct2Dirty = true;
                        logger.LogInformation(
                            "Resumed: {Artist} - {Track} @ {PosS}s (re-synced to source)",
                            artist, track, Math.Round(positionMs / 1000.0));
                    }
                    // else: already playing -- isPaused already false, pausedAt already 0
                }

                // B8: Duration update on heartbeat (server.js lines 991-998)
                if (durationMs > 0)
                {
                    long prevDur = (long?)ct2["duration"]?.GetValue<long>() ?? 0L;
                    if (durationMs != prevDur)
                    {
                        if (prevDur == 0L)
                            logger.LogInformation("Duration set from webhook: {DurS}s", Math.Round(durationMs / 1000.0));
                        else if (Math.Abs(durationMs - prevDur) > 1000L)
                            logger.LogInformation(
                                "Duration updated from webhook: {OldS}s -> {NewS}s",
                                Math.Round(prevDur / 1000.0), Math.Round(durationMs / 1000.0));
                        ct2["duration"] = JsonValue.Create(durationMs);
                        ct2Dirty = true;
                    }
                }

                // B7: Seek handling (server.js lines 1004-1009)
                // Fires AFTER pause/resume block. Only when not paused.
                //
                // Phase M #1A: cooldown-gated for browser sources only.
                // YouTube ad insertions trigger this twice in quick succession
                // (start + end of ad).  Coalescing reduces the OBS-bar
                // double-snap to a single resync; the post-ad correct
                // position lands via B10 below as soon as the cooldown clears
                // and drift > 4 s is detected.
                bool b7Cooldown = IsBrowserLikeSource(source)
                    && state.LastStartedAtUpdateMs > 0
                    && (nowMs - state.LastStartedAtUpdateMs) < MinResyncIntervalMs;
                if (isSeek && ct2["isPaused"]?.GetValue<bool>() != true && !b7Cooldown)
                {
                    long expectedPos = nowMs - ((long?)ct2["startedAt"]?.GetValue<long>() ?? nowMs);
                    long drift7 = Math.Abs(expectedPos - positionMs);
                    // Stage 7.15 Fix B: defensive guard against false-positive isSeek payloads.
                    // If the tray (Fix A1 / A2) ever regresses or a future SMTC quirk
                    // re-introduces false isSeek=true, this server-side check prevents the
                    // resulting startedAt corruption that freezes the overlay clock. Real
                    // user-initiated seeks have drift7 >> 500 ms; false positives from a
                    // frozen-position SMTC source present as drift7 ~= 0.
                    // See V14_S7_14_DIAG_OVERLAY_TIME_FROZEN.md sections 5.3 + 8.2.
                    // LogDebug (not LogInformation) so we don't refill server.log at the
                    // same 3-4 Hz rate the false-positive cascade currently does.
                    if (drift7 < 500)
                    {
                        logger.LogDebug(
                            "B7: isSeek=true but drift7={Drift}ms < 500ms -- ignoring (likely false positive from stale SMTC)",
                            drift7);
                    }
                    else
                    {
                        ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
                        ct2Dirty = true;
                        state.LastStartedAtUpdateMs = nowMs;
                        logger.LogInformation(
                            "Seek ({DriftS}s jump) -- startedAt resynced to pos={PosS}s",
                            Math.Round(drift7 / 1000.0), Math.Round(positionMs / 1000.0));
                    }
                }
                else if (isSeek && ct2["isPaused"]?.GetValue<bool>() != true && b7Cooldown)
                {
                    long age = nowMs - state.LastStartedAtUpdateMs;
                    logger.LogInformation(
                        "Seek ignored (browser cooldown: {Age}ms < {Min}ms) [{Source}]",
                        age, MinResyncIntervalMs, source);
                }

                // B9: First-heartbeat correction (server.js lines 1014-1023)
                // Only when: !isSeek && !isPaused && positionMs > 0 && _setAt present
                long setAt = (long?)ct2["_setAt"]?.GetValue<long>() ?? 0L;
                if (!isSeek && !isPaused && positionMs > 0 && setAt > 0)
                {
                    long timeSinceSet = nowMs - setAt;
                    if (timeSinceSet < 2000)
                    {
                        long computedElapsed9 = nowMs - ((long?)ct2["startedAt"]?.GetValue<long>() ?? nowMs);
                        long drift9 = Math.Abs(computedElapsed9 - positionMs);
                        if (drift9 > 5000)
                        {
                            ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
                            ct2Dirty = true;
                            logger.LogInformation(
                                "First-heartbeat correction [{Source}]: {DriftS}s drift -> resynced to {PosS}s",
                                source, Math.Round(drift9 / 1000.0), Math.Round(positionMs / 1000.0));
                        }
                    }
                }

                // B10: Continuous drift correction (server.js lines 1038-1050)
                // Only when: !isSeek && !isPaused && positionMs > 1000
                //
                // Phase M #1A: cooldown-gated for browser sources, same logic
                // as B7.  Critically B10 is ALSO what catches up the position
                // ~50 ms after a B7 cooldown expires (e.g. end of a YouTube ad
                // — B7 was skipped, B10 sees the drift > 4 s and resyncs once
                // the cooldown has elapsed).  So the cooldown coalesces rapid
                // bursts but doesn't permanently strand the bar.
                if (!isSeek && !isPaused && positionMs > 1000)
                {
                    bool b10Cooldown = IsBrowserLikeSource(source)
                        && state.LastStartedAtUpdateMs > 0
                        && (nowMs - state.LastStartedAtUpdateMs) < MinResyncIntervalMs;
                    if (b10Cooldown)
                    {
                        // Skip silently — too chatty to log at the heartbeat
                        // cadence; B7's cooldown-ignored log already shows the
                        // pattern when it matters.
                    }
                    else
                    {
                        long computedElapsed10 = nowMs - ((long?)ct2["startedAt"]?.GetValue<long>() ?? nowMs);
                        long signedDrift = positionMs - computedElapsed10; // + = SMTC ahead
                        if (signedDrift > 4000)
                        {
                            ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
                            ct2Dirty = true;
                            state.LastStartedAtUpdateMs = nowMs;
                            logger.LogInformation(
                                "Sync fwd [{Source}]: overlay {DriftS}s behind -> resynced to {PosS}s",
                                source, Math.Round(signedDrift / 1000.0), Math.Round(positionMs / 1000.0));
                        }
                        // Phase Q (2026-05-17): B10 backward correction
                        // disabled for ALL sources, not just browser-like.
                        // Phase M #3A originally scoped this to
                        // youtube/youtubemusic/browser/twitch with the
                        // assumption that desktop sources (Spotify desktop,
                        // SoundCloud-RPC, Apple Music, etc.) reliably
                        // reported position.  Empirically wrong:
                        // SoundCloud-RPC is an Electron/Chromium wrapper
                        // around the SoundCloud web player; it has the
                        // exact same stale-MediaSession bug Chrome has on
                        // certain YouTube videos.  Confirmed in the log
                        // with `Sync bwd [soundcloud]` events firing every
                        // 30 s, identical sawtooth pattern.
                        //
                        // Real user-driven backward seeks are detected via
                        // the bridge / heartbeat IsSeek flag (B7 path)
                        // which doesn't care about signedDrift magnitude.
                        // So legitimate backward seeks still resync
                        // startedAt — we just don't snap the overlay back
                        // to a stale reported position any more.
                        //
                        // The branch is deleted entirely rather than kept
                        // with a "never fires" guard so future readers
                        // don't think the safety net still exists.
                        // (Forward correction `signedDrift > 4000` stays —
                        // that handles legitimate "source resumed from a
                        // long pause" cases where the reported position is
                        // genuinely ahead of the overlay's compute.)
                    }
                }

                // Stage 7.30.7.1 (album-art live-refresh): the SMTC thumbnail (a data: URI in
                // webhookArt) can change mid-track -- a browser source (YouTube via Chrome) first
                // reports the app icon, then publishes the real media-session artwork a beat or
                // two later. The tray re-extracts it and the heartbeat re-sends it, but the early
                // placeholder was already stored as trackArt and B11 will not re-fire
                // (ArtResolved=true, curArt non-empty), so the overlay froze on the icon while the
                // tray showed the real art. If the incoming SMTC data: URI differs from the stored
                // art AND the stored art is itself a data: URI or empty (never clobber a resolved
                // HTTPS cover from the cascade), adopt the fresher thumbnail and broadcast.
                if (IsValidArt(webhookArt)
                    && webhookArt.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    var storedArt = (string?)ct2["trackArt"] ?? string.Empty;
                    bool storedIsDataOrEmpty = storedArt.Length == 0
                        || storedArt.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
                    if (storedIsDataOrEmpty
                        && !string.Equals(storedArt, webhookArt, StringComparison.Ordinal))
                    {
                        ct2["trackArt"] = JsonValue.Create(webhookArt);
                        ct2Dirty = true;
                        logger.LogInformation("Live SMTC thumbnail refresh -- trackArt updated from newer SMTC data URI");
                    }
                }

                // Write back only when ct2 was actually mutated --
                // skips DeepClone + ToJsonString on every same-track heartbeat where nothing changed
                if (ct2Dirty) state.CurrentTrack = ct2;

                // B11: Art retry (server.js lines 1052-1063)
                // Guard: !artResolved && !artResolving && isValidArt(data.trackArt) && !currentTrack.trackArt
                // Read art from ct2 (already a clone) to avoid an extra DeepClone via the getter
                var curArt = (string?)ct2["trackArt"];
                var curArtHttps = (string?)ct2["trackArtHttps"] ?? string.Empty;
                // v14 Phase 5: a browser source may have only a data: icon stored (curArt is data:,
                // trackArtHttps empty). Let B11 re-attempt HTTPS resolution in that case too, bounded
                // by ArtHttpsRetries so the 1s heartbeat cannot spam the multi-source cascade.
                bool wantHttpsRetry = !string.IsNullOrEmpty(curArt)
                    && curArt.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(curArtHttps)
                    && IsBrowserLikeSource(source)
                    && state.ArtHttpsRetries < 3;
                if (!state.ArtResolved && !state.ArtResolving
                    && IsValidArt(webhookArt)
                    && (string.IsNullOrEmpty(curArt) || wantHttpsRetry))
                {
                    state.ArtResolving = true;
                    if (wantHttpsRetry) state.ArtHttpsRetries++;
                    // Fire background task -- NOT awaited (mirrors JS .then() pattern)
                    // Lock released before this task runs its async work.
                    _ = ArtRetryAsync(state, artCascade, discordRpcService, artist, track, webhookArt, originUrl, source, logger);
                }
            }
        }
        finally
        {
            state.WebhookLock.Release();
        }

        // Stage 7.12 Batch B Phase E #1: REMOVED the Discord EARLY push.
        //
        // Originally we pushed twice per new track (early with placeholder art,
        // post-cascade with resolved art) so Discord showed the track ASAP.
        // But Discord's IPC enforces ~5 SET_ACTIVITY per 20 s — so 5 rapid
        // track skips × 2 writes each = 10 writes in <1 s, blowing past the
        // limit.  Discord then silently suppresses ALL updates for ~19 s
        // until the window expires.  That's the "Discord paused for 10-20 s
        // after rapid skipping" report.
        //
        // Trade-off: Discord now waits for ArtCascade (~80 ms typical, up to
        // a few seconds on slow upstreams) before showing the new track.
        // OBS overlay is unaffected — it still gets the early sseBroadcast()
        // below.  The post-cascade push at line ~417 handles Discord on its
        // own, and the always-fires PushDiscord at line ~425 covers same-track
        // state changes (pause/resume/seek).
        //
        // (was: if (needsNewTrackArtResolution) discordRpcService.PushDiscord(state.CurrentTrack);)

        // --- New track: resolve art + duration OUTSIDE the lock (server.js:889-892 Promise.all) ---
        if (needsNewTrackArtResolution)
        {
            // Both run concurrently -- mirrors server.js: await Promise.all([resolveArtwork, resolveDuration])
            // Duration resolver only runs when webhook sent duration=0 (source doesn't know it).
            var artTask = artCascade.ResolveAsync(
                resolveArtist, resolveTrack, resolveWebhookArt, originUrl, source, ct);
            var durTask = durationMs == 0
                ? durationResolver.ResolveAsync(resolveArtist, resolveTrack, ct)
                : Task.FromResult(0L);
            await Task.WhenAll(artTask, durTask);
            var (art, artHttps) = artTask.Result;
            var resolvedDurMs   = durTask.Result;

            await state.WebhookLock.WaitAsync(ct);
            try
            {
                // Guard: track may have changed while we were resolving
                // CurrentTrack getter returns a DeepClone -- must read, mutate, then set back.
                var trackClone = state.CurrentTrack;
                if (trackClone != null && IsSameTrack(resolveArtist, resolveTrack, trackClone))
                {
                    trackClone["trackArt"]      = JsonValue.Create(art ?? string.Empty);
                    trackClone["trackArtHttps"] = JsonValue.Create(artHttps ?? string.Empty);
                    // Store resolved duration only when webhook sent 0 and resolver found a value
                    if (resolvedDurMs > 0 && (long?)trackClone["duration"]?.GetValue<long>() == 0)
                        trackClone["duration"] = JsonValue.Create(resolvedDurMs);
                    state.CurrentTrack = trackClone;
                    // Only mark resolved when we actually got art; if empty, leave false so
                    // B11 art-retry can fire on a subsequent heartbeat that carries valid art.
                    // v14 Phase 5: ALSO leave it false for a browser source that got only a data:
                    // icon and no HTTPS cover, so the bounded B11 retry keeps trying to resolve an
                    // HTTPS cover (e.g. via the now-ungated YouTubeSource) on later heartbeats.
                    bool gotPrimaryNew = !string.IsNullOrEmpty(art);
                    bool browserIconOnly = gotPrimaryNew && string.IsNullOrEmpty(artHttps)
                        && IsBrowserLikeSource(source)
                        && (art ?? string.Empty).StartsWith("data:", StringComparison.OrdinalIgnoreCase);
                    state.ArtResolved  = gotPrimaryNew && !browserIconOnly;
                    logger.LogInformation(
                        "Track ready -- art: {ArtStatus} (httpsArt: {HttpsStatus})  duration: {DurStatus}",
                        string.IsNullOrEmpty(art) ? "no" : "yes",
                        string.IsNullOrEmpty(artHttps) ? "no" : "yes",
                        (long?)trackClone["duration"]?.GetValue<long>() > 0 ? "yes" : "not found");
                }
                state.ArtResolving = false;
            }
            finally
            {
                state.WebhookLock.Release();
            }
            // Broadcast with art (mirrors server.js setTrack line 898: sseBroadcast after art)
            // Use CurrentTrackJson (cached serialization) -- avoids re-cloning the large JSON node.
            state.Broadcast(state.CurrentTrackJson);
            // Discord post-cascade push: sends final art URL to Discord
            discordRpcService.PushDiscord(state.CurrentTrack);
        }

        // --- sseBroadcast() (server.js line 1065 -- always fires) ---
        // Stage 7.12 Batch B Phase C #6: BroadcastIfChanged skips the broadcast
        // when the serialized JSON matches the previous frame (the dominant
        // heartbeat case — 10/s identical payloads at 100 ms heartbeat cadence).
        // Real state changes (pause/seek/track change) always go through
        // because the JSON differs.  Discord push has its own dedup inside
        // DiscordRpcService.PushDiscord, so it costs ~1 µs on a no-op.
        state.BroadcastIfChanged(state.CurrentTrackJson);
        discordRpcService.PushDiscord(state.CurrentTrack);

        // --- 200 OK (server.js: res.writeHead(200); res.end('OK')) ---
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.Body.WriteAsync(s_okBytes, ct);
    }

    // ── B11 background art retry ──────────────────────────────────────────────
    // Mirrors server.js lines 1052-1063: resolveArtwork().then(art => { ... sseBroadcast() })
    // Fire-and-forget -- called without await from the main handler.
    private static async Task ArtRetryAsync(
        ServerState state,
        ArtCascade artCascade,
        DiscordRpcService discordRpcService,
        string artist, string track,
        string webhookArt, string originUrl, string source,
        ILogger<Program> logger)
    {
        try
        {
            var (art, artHttps) = await artCascade.ResolveAsync(
                artist, track, webhookArt, originUrl, source, CancellationToken.None);

            // v14 Phase 5: "usable" = a real HTTPS cover OR a non-data: primary art. For BROWSER
            // sources a bare data: icon is NOT usable (keep it but do not latch, so a bounded retry
            // can keep trying for an HTTPS cover -- capped by ArtHttpsRetries in the B11 guard). For
            // non-browser sources, behavior is unchanged: any non-empty art is terminal.
            bool isBrowserRetry = IsBrowserLikeSource(source);
            bool usable = isBrowserRetry
                ? (!string.IsNullOrEmpty(artHttps)
                   || (!string.IsNullOrEmpty(art)
                       && !art.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                : (!string.IsNullOrEmpty(art) || !string.IsNullOrEmpty(artHttps));

            if (!string.IsNullOrEmpty(art) || !string.IsNullOrEmpty(artHttps))
            {
                await state.WebhookLock.WaitAsync(CancellationToken.None);
                try
                {
                    // CurrentTrack getter returns a DeepClone -- must read, mutate, then set back.
                    var retryClone = state.CurrentTrack;
                    if (retryClone != null && IsSameTrack(artist, track, retryClone))
                    {
                        retryClone["trackArt"]      = JsonValue.Create(art);
                        retryClone["trackArtHttps"] = JsonValue.Create(artHttps);
                        state.CurrentTrack = retryClone;
                        // Latch resolved only on a usable result; an icon-only browser result leaves
                        // it false so a further bounded retry can run (capped by ArtHttpsRetries).
                        state.ArtResolved = usable;
                        logger.LogInformation(
                            "Art updated from webhook retry (usable={Usable}, https={Https})",
                            usable, string.IsNullOrEmpty(artHttps) ? "no" : "yes");
                    }
                }
                finally
                {
                    state.ArtResolving = false;
                    state.WebhookLock.Release();
                }
                state.Broadcast(state.CurrentTrackJson);
                // Discord B11 push: final art URL now known
                discordRpcService.PushDiscord(state.CurrentTrack);
            }
            else
            {
                // Art retry found nothing at all. Mark ArtResolved=true so the B11 guard stops
                // re-triggering this retry every heartbeat. Without this circuit-breaker the
                // server accumulates parallel cascade tasks (multiple HTTP sources × 1s heartbeat
                // cadence) causing unbounded memory growth.
                // ArtResolved resets to false on the next distinct track, so a fresh cascade
                // will run for each new title.
                await state.WebhookLock.WaitAsync(CancellationToken.None);
                try { state.ArtResolving = false; state.ArtResolved = true; }
                finally { state.WebhookLock.Release(); }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Art retry error for {Artist} - {Track}", artist, track);
            // Also set ArtResolved=true so B11 doesn't re-fire on every heartbeat
            // after an unexpected exception (ArtCascade should never throw, but be safe).
            await state.WebhookLock.WaitAsync(CancellationToken.None);
            try { state.ArtResolving = false; state.ArtResolved = true; }
            finally { state.WebhookLock.Release(); }
        }
    }

    // ── Stubs removed in sub-stage 4.9e ──────────────────────────────────────
    // ResolveArtworkAsync stub (4.4) replaced by artCascade.ResolveAsync above.
    // ResolveDurationAsync stub (4.4) replaced by durationResolver.ResolveAsync above.

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors server.js trackKey (line 339-341).
    /// Case-insensitive, whitespace-normalized key for same-track detection.
    /// </summary>
    public static string TrackKey(string artist, string track)
        => $"{artist.Trim().ToLowerInvariant()}|||{track.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Mirrors server.js isSameTrack (lines 343-345).
    /// Returns false if no currentTrack. Compares trackKeys.
    /// </summary>
    public static bool IsSameTrack(string artist, string track, JsonNode? currentTrack)
    {
        if (currentTrack == null) return false;
        var curArtist = (string?)currentTrack["artist"] ?? string.Empty;
        var curTrack  = (string?)currentTrack["track"]  ?? string.Empty;
        return TrackKey(curArtist, curTrack) == TrackKey(artist, track);
    }

    /// <summary>
    /// Mirrors server.js isValidArt (lines 411-414).
    /// Returns false for null/empty, Last.fm generic placeholder, or default_avatar.
    /// </summary>
    public static bool IsValidArt(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.Contains("2a96cbd8b46e442fc41c2b86b821562f")) return false; // Last.fm placeholder
        if (url.Contains("default_avatar")) return false;
        return true;
    }
}
