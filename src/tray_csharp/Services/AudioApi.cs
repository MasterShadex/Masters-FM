// INTERRUPT #3 STEP 7 (Issue 2): MME audio device enumeration via winmm.dll
// P/Invoke.  Provides the MME backend device list that the WinRT path cannot
// surface (DeviceClass.AudioRender returns WASAPI devices only).
//
// KS devices are surfaced by the existing WinRT enumeration path in
// AudioDeviceViewModel; no separate KS P/Invoke is needed for rc.3 scope.
// ASIO remains deferred to a future brief per INTERRUPT #3 absolute constraint.

using System.Runtime.InteropServices;

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
}
