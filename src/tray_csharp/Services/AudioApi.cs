// INTERRUPT #3 STEP 7 (Issue 2): MME audio device enumeration via winmm.dll
// P/Invoke.  Provides the MME backend device list that the WinRT path cannot
// surface (DeviceClass.AudioRender returns WASAPI devices only).
//
// Stage 7.12 Batch B Phase K (DIAG 04): added ASIO driver enumeration via
// registry (HKLM\SOFTWARE\ASIO\<DriverName>) and the WOW6432Node mirror.
// KS devices share the same WinRT capture-endpoint enumeration as WASAPI
// inputs (audio_spectrum.cs treats backend="wdm_ks" and "wasapi_exclusive"
// identically — same MMDevice IDs, just opened in exclusive mode) — so the
// KS list lives in AudioDeviceViewModel where the WinRT call already runs.

using Microsoft.Win32;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Tray.Services;

public static class AudioApi
{
    // ------------------------------------------------------------------
    // MME P/Invoke surface (winmm.dll)
    // ------------------------------------------------------------------

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(
        int uDeviceID, ref WaveOutCaps pwoc, int cbwoc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint   vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint   dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint   dwSupport;
    }

    // ------------------------------------------------------------------
    // Public data contract
    // ------------------------------------------------------------------

    public readonly struct MmeDevice
    {
        public int    Index    { get; init; }
        public string Name     { get; init; }
        public int    Channels { get; init; }
    }

    // ------------------------------------------------------------------
    // Enumeration
    // ------------------------------------------------------------------

    /// <summary>
    /// Enumerate MME wave-out (playback) devices via winmm.dll.
    /// Returns an empty list if winmm.dll is unavailable or no devices exist.
    /// Device index 0 is the system-preferred (default) MME playback device.
    /// </summary>
    public static IReadOnlyList<MmeDevice> EnumerateMmeOutputDevices()
    {
        var result = new List<MmeDevice>();
        try
        {
            int count = waveOutGetNumDevs();
            for (int i = 0; i < count; i++)
            {
                var caps = new WaveOutCaps();
                int mmsysErr = waveOutGetDevCaps(i, ref caps, Marshal.SizeOf(caps));
                if (mmsysErr == 0) // MMSYSERR_NOERROR
                {
                    result.Add(new MmeDevice
                    {
                        Index    = i,
                        Name     = string.IsNullOrWhiteSpace(caps.szPname)
                                       ? $"MME Output {i}"
                                       : caps.szPname,
                        Channels = caps.wChannels,
                    });
                }
            }
        }
        catch
        {
            // winmm.dll unavailable on very restricted environments; return empty.
        }
        return result;
    }

    // ------------------------------------------------------------------
    // ASIO driver enumeration (Stage 7.12 Batch B Phase K / DIAG 04)
    // ------------------------------------------------------------------
    //
    // ASIO drivers register themselves under HKEY_LOCAL_MACHINE\SOFTWARE\ASIO\
    // (the canonical 64-bit hive).  A 32-bit installer on 64-bit Windows
    // ends up under HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\ASIO\, so we
    // enumerate BOTH and deduplicate by driver-name.
    //
    // Each subkey is one driver, named after the human-readable driver name
    // (e.g. "ASIO4ALL v2", "FL Studio ASIO", "VB-Audio Matrix VASIO").
    // Inside the subkey:
    //   CLSID        = "{xxxx-xxxx-…}"     -- required, the COM ID
    //   Description  = string (often)      -- prettier name; falls back to subkey
    //
    // We don't ACTIVATE the COM object here — that's the audio_spectrum's
    // job when the user picks one.  All we do is list what's installed.

    public readonly struct AsioDriver
    {
        public string Name        { get; init; }   // registry subkey name (used as the ID)
        public string Description { get; init; }   // optional pretty name
        public string Clsid       { get; init; }   // GUID string, empty if unreadable
    }

    /// <summary>
    /// Enumerate installed ASIO drivers from the system registry.
    /// Reads HKLM\SOFTWARE\ASIO (native 64-bit) AND HKLM\SOFTWARE\WOW6432Node\ASIO
    /// (32-bit mirror), deduplicates by driver name, sorted alphabetically.
    /// Returns an empty list if no drivers are installed or the registry is
    /// inaccessible (which on a normal Windows install effectively means
    /// "no ASIO drivers" — the key only exists when an ASIO host or driver
    /// has been installed).
    /// </summary>
    public static IReadOnlyList<AsioDriver> EnumerateAsioDrivers()
    {
        var byName = new Dictionary<string, AsioDriver>(StringComparer.OrdinalIgnoreCase);
        ReadAsioHive(RegistryView.Registry64, byName);
        ReadAsioHive(RegistryView.Registry32, byName);
        var list = new List<AsioDriver>(byName.Values);
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }

    /// <summary>
    /// Ask the running audio_spectrum process for its enumerated ASIO entries.
    /// audio_spectrum probes each registered driver via NAudio's AsioOut for
    /// the real channel count and emits one entry per stereo channel pair
    /// (e.g. "VB-Matrix VASIO-32  -  Ch 5-6" with id "VB-Matrix VASIO-32|4").
    /// The compound id is exactly what audio_spectrum's set-device endpoint
    /// expects to receive back — so round-tripping is byte-identical.
    ///
    /// Returns an empty list if audio_spectrum isn't reachable (e.g. the
    /// dialog opened during the brief startup window before the child
    /// process is listening).  The caller should fall back to the
    /// registry-only path via <see cref="EnumerateAsioDrivers"/>.
    /// </summary>
    public static async Task<IReadOnlyList<AsioDriver>> FetchAsioFromSpectrumAsync(
        HttpClient http, CancellationToken ct = default)
    {
        const string url = "http://127.0.0.1:4243/devices";
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, linked.Token);
            if (!resp.IsSuccessStatusCode) return Array.Empty<AsioDriver>();

            var json = await resp.Content.ReadAsStringAsync(linked.Token);
            var root = JsonNode.Parse(json);
            var arr  = root?["devices"]?.AsArray();
            if (arr == null) return Array.Empty<AsioDriver>();

            var list = new List<AsioDriver>(arr.Count);
            foreach (var node in arr)
            {
                if (node == null) continue;
                var backend = node["backend"]?.GetValue<string>();
                if (!string.Equals(backend, "asio", StringComparison.OrdinalIgnoreCase)) continue;
                var id    = node["id"]?.GetValue<string>()   ?? string.Empty;
                var name  = node["name"]?.GetValue<string>() ?? id;
                var type  = node["type"]?.GetValue<string>() ?? string.Empty;
                // audio_spectrum emits a synthetic "asio_none" marker row when
                // no drivers are installed — skip it; the empty-state UI panel
                // handles that case.
                if (string.Equals(type, "asio_none", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new AsioDriver
                {
                    Name        = id,    // compound id (driver|offset) — used as DeviceId by the tray
                    Description = name,  // pretty display (e.g. "VB-Matrix VASIO-32  -  Ch 5-6")
                    Clsid       = string.Empty,
                });
            }
            return list;
        }
        catch
        {
            // Network unreachable / timeout / parse failure → fall back to registry path.
            return Array.Empty<AsioDriver>();
        }
    }

    private static void ReadAsioHive(RegistryView view, Dictionary<string, AsioDriver> sink)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var asio = hklm.OpenSubKey(@"SOFTWARE\ASIO");
            if (asio == null) return;
            foreach (var subKeyName in asio.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(subKeyName)) continue;
                try
                {
                    using var sub = asio.OpenSubKey(subKeyName);
                    if (sub == null) continue;
                    var clsid       = (sub.GetValue("CLSID") as string) ?? string.Empty;
                    var description = (sub.GetValue("Description") as string) ?? string.Empty;
                    if (sink.ContainsKey(subKeyName)) continue; // 64-bit wins over 32-bit
                    sink[subKeyName] = new AsioDriver
                    {
                        Name        = subKeyName,
                        Description = description,
                        Clsid       = clsid,
                    };
                }
                catch { /* one bad subkey shouldn't kill the whole enum */ }
            }
        }
        catch
        {
            // Registry hive unavailable (rare; mostly locked-down enterprise).
            // Empty enumeration = "no ASIO drivers", which is the correct UX.
        }
    }
}
