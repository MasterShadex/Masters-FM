// Stage 7.2: UpdateCheckService. 6-state machine port from
// tray.ps1:5187-5798 (S12). Closes R6 via explicit pre-release regex
// (SemVerComparer.IsPreRelease). Closes B-003 via ProcessStartInfo +
// ArgumentList for install handoff. Authenticode verification is
// stricter than PS S12 (chain validation REQUIRED; CN exact match).

using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Windows;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Update;

public sealed class UpdateCheckService : IUpdateCheckService
{
    // Stage 7.31.2: the manifest base repo is resolved via a DEFAULT-SAFE opt-in override
    // (see ResolveBaseRepo). With NO override present, this is byte-identical to production
    // (raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json). The production
    // default string is unchanged; the override only exists for end-to-end testing against a
    // throwaway repo and its VALUES live outside the build so they can never ship.
    private const string DefaultBaseRepo = "MasterShadex/Masters-FM";
    private static string ManifestUrl =>
        $"https://raw.githubusercontent.com/{ResolveBaseRepo()}/main/version.json";
    private const string ExpectedCertCn = "MasterShadex";
    // Stage 7.31.1: cert PIN. The auto-updater pins to OUR exact self-signed publisher cert
    // (thumbprint + RSA public key) instead of requiring the verifier machine to trust it as a
    // root. Fixes 7.31 HARDEN-FIRST: chain.Build returned UntrustedRoot on any machine without the
    // cert in Trusted Root (i.e. every friend), so legit MSIs were rejected and auto-update never
    // reached anyone but the operator. Pinning is machine-trust-independent yet strict -- a
    // different / forged / unsigned signer is still rejected. MAINTENANCE: if the signing cert is
    // ever regenerated (expiry 2031-04-21, or deleted so _sign_msi.ps1 mints a new one), update
    // BOTH constants below or auto-update will reject the newly-signed MSIs.
    private const string PinnedThumbprint   = "4B1660FC0B77F55C7D47B3B9010C873E5CC2B2BF";
    private const string PinnedPublicKeyHex = "3082010A0282010100B3FD3ABD461E3EF30E94471C628120BB5D30B12A7B95083A9FC345F7C916F54E3CCA509ABE95BFE940CC65213426994883B1462C847BEF4144EE9CCDC85B952728DE71D401F9ED6BBB258174B70BCEDAB0D2549BAB54D1B472E11D655B7E30DF9A3519E89ACDDCF850DF8B31589514BE01D8101CA280034A90D6765770FD0591BF3C1F497AE949629254212E2B66B0739502F885753C3B9213207CF46B214A392F8EC0CF84EA34FF9036C850A40DD7512CC41412BC0E2E76AFA5617D26D0AE5B1439DE98F566434CDA3B6B2265403256498BA3BC8B6F4DA6F468E9C8849035127F3DD223FF3155F00128DACE654653BA1D8B7EF00ABE723D2ED3B27F0F368B010203010001";
    private const string LastCheckedConfigKey = "update.lastChecked";
    private const string Component = "Update";
    private static readonly TimeSpan StartupCheckCadence = TimeSpan.FromHours(6);

    private readonly ILogger _logger;
    private readonly IConfigService _config;
    private readonly ITelemetry _telemetry;
    private readonly HttpClient _httpClient;

    private readonly object _lock = new();
    private UpdateState _currentState = UpdateState.Idle;
    private string? _availableVersion;
    private string? _msiUrl;
    private string? _msiSha256;
    private bool _autoInstall;
    private string? _msiPath;
    private string? _lastErrorMessage;
    private CancellationTokenSource? _cts;

    public UpdateState CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    public string? AvailableVersion
    {
        get { lock (_lock) return _availableVersion; }
    }

    public DateTime? LastCheckedUtc
    {
        get
        {
            var s = _config.GetValue<string>(LastCheckedConfigKey);
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt.ToUniversalTime();
            }
            return null;
        }
    }

    public string? LastErrorMessage
    {
        get { lock (_lock) return _lastErrorMessage; }
    }

    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    public UpdateCheckService(ILogger logger, IConfigService config, ITelemetry telemetry, HttpClient httpClient)
    {
        _logger = logger;
        _config = config;
        _telemetry = telemetry;
        _httpClient = httpClient;
    }

    public async Task<UpdateState> CheckNowAsync(CancellationToken ct = default)
    {
        if (!TryTransitionTo(UpdateState.Checking, fromMust: UpdateState.Idle))
        {
            _logger.LogWarn($"CheckNow: cannot transition to Checking from {CurrentState}; ignoring", Component);
            return CurrentState;
        }

        _telemetry.IncrementCounter("update.check.attempted");
        var sw = Stopwatch.StartNew();

        try
        {
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_lock) { _cts = localCts; }

            var cacheBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url = $"{ManifestUrl}?t={cacheBust}";
            _logger.Log($"check fired: GET {url}", Component);

            var json = await _httpClient.GetStringAsync(url, localCts.Token).ConfigureAwait(false);
            var resultState = ProcessManifestJson(json);

            // Persist last-checked timestamp (success path; even when no update)
            _config.SetValue<string>(LastCheckedConfigKey, DateTime.UtcNow.ToString("o"));
            _telemetry.RecordTimingMs("update.check.duration", sw.Elapsed.TotalMilliseconds);

            return resultState;
        }
        catch (OperationCanceledException)
        {
            _logger.Log("check cancelled", Component);
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }
        catch (Exception ex)
        {
            _logger.LogErr("CheckNowAsync HTTP", ex, Component);
            lock (_lock) { _lastErrorMessage = ex.Message; }
            TransitionTo(UpdateState.Error, detail: ex.Message);
            return UpdateState.Error;
        }
        finally
        {
            lock (_lock) { _cts = null; }
        }
    }

    /// <summary>
    /// Internal: processes a manifest JSON string and drives state transitions.
    /// Factored out of CheckNowAsync so tests can drive synthetic payloads
    /// without making real HTTP calls (per brief STEP 8.7 mocked-HTTP testing).
    /// </summary>
    internal UpdateState ProcessManifestJson(string jsonText)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonText);
        }
        catch (Exception ex)
        {
            _logger.LogErr("manifest JSON parse", ex, Component);
            lock (_lock) { _lastErrorMessage = "Manifest JSON parse failure"; }
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }

        if (root == null)
        {
            _logger.LogWarn("manifest JSON parsed to null root; treating as no update", Component);
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }

        var remoteVersion = root["version"]?.GetValue<string>();
        var remoteMsiUrl = root["msi_url"]?.GetValue<string>();
        var remoteMsiSha = root["msi_sha256"]?.GetValue<string>();
        bool remoteAutoInstall = false;
        try { remoteAutoInstall = root["autoInstall"]?.GetValue<bool>() ?? false; } catch { /* keep false */ }

        if (string.IsNullOrEmpty(remoteVersion))
        {
            _logger.LogWarn("manifest has no 'version' field; treating as no update", Component);
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }

        // R6 closure: explicit pre-release rejection BEFORE comparison.
        if (SemVerComparer.IsPreRelease(remoteVersion))
        {
            _logger.Log($"remote version {remoteVersion} is pre-release; auto-update disabled by policy", Component);
            _telemetry.IncrementCounter("update.check.rejected_prerelease");
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }

        var localVersion = GetLocalVersion();
        var cmp = SemVerComparer.Compare(remoteVersion, localVersion);

        if (cmp == int.MinValue)
        {
            _logger.LogWarn($"version comparison failed (remote={remoteVersion} local={localVersion}); treating as no update", Component);
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }

        if (cmp <= 0)
        {
            _logger.Log($"up-to-date (remote={remoteVersion} local={localVersion})", Component);
            TransitionTo(UpdateState.Idle);
            return UpdateState.Idle;
        }

        // Remote stable > local; transition to Available.
        lock (_lock)
        {
            _availableVersion = remoteVersion;
            _msiUrl = remoteMsiUrl;
            _msiSha256 = remoteMsiSha;
            _autoInstall = remoteAutoInstall;
        }
        _telemetry.IncrementCounter("update.check.found");
        _logger.Log($"update available: v{remoteVersion} (local={localVersion}, autoInstall={remoteAutoInstall}, msi={remoteMsiUrl})", Component);
        TransitionTo(UpdateState.Available, detail: remoteVersion);
        return UpdateState.Available;
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        if (!TryTransitionTo(UpdateState.Downloading, fromMust: UpdateState.Available))
        {
            _logger.LogWarn($"Download: cannot transition to Downloading from {CurrentState}; ignoring", Component);
            return;
        }

        var url = _msiUrl;
        var version = _availableVersion;
        var expectedSha = _msiSha256;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(version))
        {
            _logger.LogWarn("Download: msi_url or version missing", Component);
            TransitionTo(UpdateState.Available);
            return;
        }

        var downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MastersFM", "downloads");
        Directory.CreateDirectory(downloadDir);
        var partialPath = Path.Combine(downloadDir, $"MastersFM_update_v{version}.msi.partial");
        var finalPath = Path.Combine(downloadDir, $"MastersFM_update_v{version}.msi");

        try
        {
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_lock) { _cts = localCts; }

            using (var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, localCts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
                using var src = await resp.Content.ReadAsStreamAsync(localCts.Token).ConfigureAwait(false);
                using var dst = File.Create(partialPath);
                var buf = new byte[81920];
                long received = 0;
                int read;
                int lastReportedPct = -1;
                while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), localCts.Token).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), localCts.Token).ConfigureAwait(false);
                    received += read;
                    if (totalBytes > 0)
                    {
                        var pct = (int)(received * 100L / totalBytes);
                        if (pct != lastReportedPct && pct % 5 == 0)
                        {
                            FireStateChanged(UpdateState.Downloading, UpdateState.Downloading, pct, $"{received}/{totalBytes} bytes");
                            lastReportedPct = pct;
                        }
                    }
                }
            }

            // Verify SHA256
            if (!string.IsNullOrEmpty(expectedSha))
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(partialPath);
                var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
                var actual = Convert.ToHexString(hash).ToLowerInvariant();
                if (!string.Equals(actual, expectedSha.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    _logger.LogErr($"SHA256 mismatch: actual={actual} expected={expectedSha.ToLowerInvariant()}; deleting download", null, Component);
                    try { File.Delete(partialPath); } catch { }
                    lock (_lock) { _lastErrorMessage = "SHA256 mismatch"; }
                    TransitionTo(UpdateState.Available);
                    return;
                }
            }

            // Verify Authenticode (stricter than PS: chain validation REQUIRED, CN exact match)
            if (!VerifyAuthenticode(partialPath, out var authError))
            {
                _logger.LogErr($"Authenticode verification failed: {authError}; deleting download", null, Component);
                try { File.Delete(partialPath); } catch { }
                lock (_lock) { _lastErrorMessage = $"Authenticode: {authError}"; }
                TransitionTo(UpdateState.Available);
                return;
            }

            // Atomic rename to final path
            File.Move(partialPath, finalPath, overwrite: true);
            lock (_lock) { _msiPath = finalPath; }
            _logger.Log($"download + verify complete: {finalPath}", Component);
            TransitionTo(UpdateState.Ready, detail: version);

            // Auto-install path (per autoInstall flag in manifest)
            if (_autoInstall)
            {
                _logger.Log("autoInstall=true; chaining to InstallAsync", Component);
                _ = InstallAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Log("download cancelled; deleting partial", Component);
            try { File.Delete(partialPath); } catch { }
            TransitionTo(UpdateState.Available);
        }
        catch (Exception ex)
        {
            _logger.LogErr("DownloadAsync", ex, Component);
            lock (_lock) { _lastErrorMessage = ex.Message; }
            try { File.Delete(partialPath); } catch { }
            TransitionTo(UpdateState.Available);
        }
        finally
        {
            lock (_lock) { _cts = null; }
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _cts; }
        try { cts?.Cancel(); } catch { }
        TransitionTo(UpdateState.Idle);
    }

    public Task InstallAsync(CancellationToken ct = default)
    {
        if (!TryTransitionTo(UpdateState.Installing, fromMust: UpdateState.Ready))
        {
            _logger.LogWarn($"Install: cannot transition to Installing from {CurrentState}; ignoring", Component);
            return Task.CompletedTask;
        }

        var msiPath = _msiPath;
        if (string.IsNullOrEmpty(msiPath) || !File.Exists(msiPath))
        {
            _logger.LogWarn("Install: msiPath missing", Component);
            TransitionTo(UpdateState.Available);
            return Task.CompletedTask;
        }

        try
        {
            // ProcessStartInfo with ArgumentList closes B-003 (spaces-in-path quoting bug)
            // by construction. Each ArgumentList entry is escaped per-arg by the runtime;
            // no shell interpretation. PS used a temp helper script for the same reason
            // plus to handle Major Upgrade SecureRepair (v10.1.7); brief STEP 4.8
            // chooses simpler path -- if 7.10 cutover surfaces SecureRepair, brief
            // re-introduces helper.
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(msiPath);
            psi.ArgumentList.Add("/quiet");
            psi.ArgumentList.Add("/norestart");

            _logger.Log($"install: spawning msiexec.exe with ArgumentList=[/i, '{msiPath}', /quiet, /norestart]", Component);
            Process.Start(psi);

            // Shut down the tray so the MSI can replace files.
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            _logger.LogErr("InstallAsync spawn", ex, Component);
            lock (_lock) { _lastErrorMessage = ex.Message; }
            TransitionTo(UpdateState.Ready);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies the MSI is signed by OUR exact publisher cert. Stage 7.31.1: PINNED to the
    /// MasterShadex cert by thumbprint + public key (no machine-root chain.Build dependency, so
    /// it verifies on friends' machines too); CN guard kept. A different / forged / unsigned
    /// signer is rejected. File integrity is anchored separately by the SHA256 gate in DownloadAsync.
    /// </summary>
    private bool VerifyAuthenticode(string msiPath, out string error)
    {
        error = string.Empty;
        try
        {
#pragma warning disable SYSLIB0057  // X509Certificate.CreateFromSignedFile is appropriate for our use
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(msiPath));
#pragma warning restore SYSLIB0057
            // Stage 7.31.1: PIN to our exact publisher cert instead of requiring machine-root trust.
            // CreateFromSignedFile above already threw if the file is unsigned. Require the signer to
            // be EXACTLY our MasterShadex cert by thumbprint AND public key -- machine-trust
            // independent (no chain.Build / Trusted-Root dependency, so it works on every friend's
            // machine) yet strict (a different / forged signer is rejected). File integrity stays
            // anchored by the SHA256 gate in DownloadAsync, which runs before this.
            if (!string.Equals(cert.Thumbprint, PinnedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                error = $"signer thumbprint '{cert.Thumbprint}' != pinned '{PinnedThumbprint}'";
                return false;
            }
            if (!string.Equals(cert.GetPublicKeyString(), PinnedPublicKeyHex, StringComparison.OrdinalIgnoreCase))
            {
                error = "signer public key does not match the pinned MasterShadex key";
                return false;
            }
            // CN exact match (defense-in-depth alongside the pin)
            var subject = cert.Subject;
            if (!subject.Contains($"CN={ExpectedCertCn}", StringComparison.Ordinal))
            {
                error = $"unexpected subject '{subject}' (expected CN={ExpectedCertCn})";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"exception: {ex.Message}";
            return false;
        }
    }

    // Stage 7.31.2: DEFAULT-SAFE opt-in base-repo override for end-to-end update testing.
    // Resolution order: env var MASTERSFM_UPDATE_BASE_REPO, then %LOCALAPPDATA%\MastersFM\
    // updater_override.json {"baseRepo":"owner/repo"}, else DefaultBaseRepo (production). Absent any
    // override -> production (identical to before). Override VALUES live on the dev machine only and
    // are never shipped. The cert PIN + SHA256 gate still apply regardless of base repo, so a
    // redirect cannot install a non-MasterShadex MSI. Validated owner/repo only (else default).
    private static bool IsValidBaseRepo(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
        foreach (var ch in s)
            if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' || ch == '/'))
                return false;
        return true;
    }

    private static string ResolveBaseRepo()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("MASTERSFM_UPDATE_BASE_REPO");
            if (env != null && IsValidBaseRepo(env.Trim())) return env.Trim();
            var ovr = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MastersFM", "updater_override.json");
            if (File.Exists(ovr))
            {
                var repo = JsonNode.Parse(File.ReadAllText(ovr))?["baseRepo"]?.GetValue<string>();
                if (repo != null && IsValidBaseRepo(repo.Trim())) return repo.Trim();
            }
        }
        catch { /* any error -> fall through to the safe production default */ }
        return DefaultBaseRepo;
    }

    private static string GetLocalVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        var ver = asm.GetName().Version;
        return ver?.ToString() ?? "0.0.0";
    }

    private void TransitionTo(UpdateState newState, int? progressPercent = null, string? detail = null)
    {
        UpdateState old;
        lock (_lock)
        {
            old = _currentState;
            _currentState = newState;
        }
        _logger.Log($"state {old} -> {newState}{(detail != null ? $" ({detail})" : "")}", Component);
        FireStateChanged(old, newState, progressPercent, detail);
    }

    private bool TryTransitionTo(UpdateState newState, UpdateState fromMust)
    {
        lock (_lock)
        {
            if (_currentState != fromMust) return false;
            _currentState = newState;
        }
        _logger.Log($"state {fromMust} -> {newState}", Component);
        FireStateChanged(fromMust, newState);
        return true;
    }

    private void FireStateChanged(UpdateState old, UpdateState newState, int? progressPercent = null, string? detail = null)
    {
        try
        {
            StateChanged?.Invoke(this, new UpdateStateChangedEventArgs(old, newState, progressPercent, detail));
        }
        catch (Exception ex)
        {
            _logger.LogErr("StateChanged subscriber", ex, Component);
        }
    }
}
