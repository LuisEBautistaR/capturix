using System.Runtime.InteropServices;

namespace Capturix.Core.Recording;

// Minimal WASAPI (Core Audio) interop: default endpoint lookup and shared-mode capture,
// including loopback capture of what the speakers play.

internal static class Wasapi
{
    public static readonly Guid CLSID_MMDeviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    public static readonly Guid IID_IAudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    public static readonly Guid IID_IAudioCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    public const int eRender = 0;
    public const int eCapture = 1;
    public const int eConsole = 0;
    public const int CLSCTX_ALL = 0x17;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }
}

/// <summary>The parts of WAVEFORMAT(EXTENSIBLE) needed to interpret captured buffers.</summary>
internal readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int BlockAlign)
{
    public static AudioFormat FromPointer(IntPtr waveFormat)
    {
        ushort tag = (ushort)Marshal.ReadInt16(waveFormat, 0);
        int channels = (ushort)Marshal.ReadInt16(waveFormat, 2);
        int rate = Marshal.ReadInt32(waveFormat, 4);
        int blockAlign = (ushort)Marshal.ReadInt16(waveFormat, 12);
        int bits = (ushort)Marshal.ReadInt16(waveFormat, 14);
        bool isFloat = tag == Wasapi.WAVE_FORMAT_IEEE_FLOAT;
        if (tag == Wasapi.WAVE_FORMAT_EXTENSIBLE)
        {
            // WAVEFORMATEX (18 bytes) + valid bits (2) + channel mask (4) + SubFormat GUID (16).
            var sub = new byte[16];
            Marshal.Copy(waveFormat + 24, sub, 0, 16);
            isFloat = new Guid(sub) == Wasapi.SubtypeIeeeFloat;
        }
        return new AudioFormat(rate, channels, bits, isFloat, blockAlign);
    }
}

[ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints();
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice(); void RegisterEndpointNotificationCallback(); void UnregisterEndpointNotificationCallback();
}

[ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void OpenPropertyStore(); void GetId(); void GetState();
}

[ComImport, Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    void GetBufferSize(); void GetStreamLatency(); void GetCurrentPadding(); void IsFormatSupported();
    [PreserveSig] int GetMixFormat(out IntPtr format);
    void GetDevicePeriod();
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    void Reset(); void SetEventHandle();
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("c8adbd64-e71e-48a0-a4de-185c395cd317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(int frames);
    [PreserveSig] int GetNextPacketSize(out int frames);
}
