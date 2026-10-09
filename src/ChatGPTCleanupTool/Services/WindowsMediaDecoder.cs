using System.IO;
using System.Runtime.InteropServices;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

/// <summary>Windows Media Foundation Source Reader. No external executable, playback, or network URL is used.</summary>
internal sealed unsafe class WindowsMediaDecoder : IDisposable
{
    private IntPtr _reader;
    private bool _started, _uninitialize;
    private readonly uint _stream;
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private int _width, _height, _stride, _rate, _channels;
    public double Duration { get; private set; }

    internal static void VerifyAvailability()
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 0); bool startup = false; IntPtr type = IntPtr.Zero;
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Check(initialized);
            Check(MFStartup(0x00020070, 0)); startup = true; Check(MFCreateMediaType(out type));
            Set32(type, Bits, 16); if (Get32(type, Bits) != 16) throw new InvalidOperationException("Windows 媒体组件验证失败");
        }
        finally { Release(ref type); if (startup) MFShutdown(); if (initialized >= 0) CoUninitialize(); }
    }

    internal WindowsMediaDecoder(string path, bool video)
    {
        DuplicatePreviewService.ValidateReadPath(path);
        _stream = video ? 0xfffffffc : 0xfffffffd;
        IntPtr attributes = IntPtr.Zero, type = IntPtr.Zero;
        try
        {
            int init = CoInitializeEx(IntPtr.Zero, 0);
            if (init >= 0) _uninitialize = true;
            else if (init != unchecked((int)0x80010106)) Check(init);
            Check(MFStartup(0x00020070, 0)); _started = true;
            Check(MFCreateAttributes(out attributes, 1));
            if (video) Set32(attributes, new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), 1);
            Check(MFCreateSourceReaderFromURL(path, attributes, out _reader));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(_reader, 4))(_reader, 0xfffffffe, 0));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(_reader, 4))(_reader, _stream, 1));
            Check(MFCreateMediaType(out type));
            SetGuid(type, Major, new Guid(video ? "73646976-0000-0010-8000-00aa00389b71" : "73647561-0000-0010-8000-00aa00389b71"));
            SetGuid(type, Subtype, new Guid(video ? "00000016-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71"));
            if (!video) Set32(type, Bits, 16);
            // Source Reader selects the source's channel count/rate; resample in managed code to avoid optional resampler dependencies.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(_reader, 7))(_reader, _stream, IntPtr.Zero, type));
            ReadFormat(video);
            var key = DurationKey; var value = new PropVariant();
            try
            {
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, PropVariant*, int>)Slot(_reader, 12))(_reader, 0xffffffff, &key, &value);
                if (hr >= 0 && value.Type is 20 or 21) Duration = value.Number / 10_000_000.0;
            }
            finally { PropVariantClear(ref value); }
        }
        catch { Dispose(); throw; }
        finally { Release(ref type); Release(ref attributes); }
    }

    private void ReadFormat(bool video)
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(_reader, 6))(_reader, _stream, &type));
            if (video)
            {
                ulong size = Get64(type, FrameSize); _width = (int)(size >> 32); _height = (int)(size & uint.MaxValue);
                if (_width <= 0 || _height <= 0 || (long)_width * _height > 67_108_864) throw new InvalidDataException("视频尺寸超过安全解码上限");
                _stride = unchecked((int)Get32(type, Stride, (uint)(_width * 4)));
                if (Math.Abs((long)_stride) < _width * 4L || Math.Abs((long)_stride) * _height > 512L * 1024 * 1024)
                    throw new InvalidDataException("视频帧布局不受支持");
            }
            else
            {
                _rate = (int)Get32(type, Rate); _channels = (int)Get32(type, Channels);
                if (Get32(type, Bits) != 16 || _rate < 8000 || _rate > 384000 || _channels < 1 || _channels > 16)
                    throw new InvalidDataException("无法取得受支持的 PCM 音频");
            }
        }
        finally { Release(ref type); }
    }

    internal MediaFingerprint ReadVideo(string path, CancellationToken token)
    {
        if (Duration < 1 || !double.IsFinite(Duration)) throw new InvalidDataException("视频无法读取有效时长（至少 1 秒）");
        var hashes = new List<ulong>(); var colors = new List<double>(); var textures = new List<double>(); var times = new List<double>();
        foreach (double fraction in new[] { .1, .3, .5, .7, .9 })
        {
            token.ThrowIfCancellationRequested();
            double target = Duration * fraction;
            var format = Guid.Empty; var position = new PropVariant { Type = 20, Number = (long)(target * 10_000_000) };
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, PropVariant*, int>)Slot(_reader, 8))(_reader, &format, &position));
            bool obtained = false;
            for (int attempt = 0; attempt < 2000; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var sample = Read(out uint flags, out long timestamp);
                try
                {
                    if ((flags & 0x20) != 0) ReadFormat(true);
                    if (sample != IntPtr.Zero && timestamp / 10_000_000.0 >= target - .05)
                    {
                        var image = ReadVideoPixels(sample);
                        var visual = MediaVisualFingerprint.Create(image.Pixels, image.Width, image.Height, trimVideoBorders: true);
                        hashes.AddRange(visual.Hashes); colors.AddRange(visual.Colors); textures.Add(visual.Texture);
                        times.Add(timestamp / 10_000_000.0); obtained = true; break;
                    }
                    if ((flags & 2) != 0) break;
                }
                finally { Release(ref sample); }
            }
            if (!obtained) throw new InvalidDataException($"无法截取 {target:F1} 秒处的视频帧");
        }
        var file = new FileInfo(path);
        return new() { Path = path, Kind = MediaKind.Video, Length = file.Length, WrittenTicks = file.LastWriteTimeUtc.Ticks,
            Width = _width, Height = _height, Duration = Duration, VisualHashes = hashes.ToArray(), Colors = colors.ToArray(),
            Texture = textures.ToArray(), SampleTimes = times.ToArray() };
    }

    private (byte[] Pixels, int Width, int Height) ReadVideoPixels(IntPtr sample)
    {
        int width = _width >= _height ? 128 : Math.Max(1, _width * 128 / _height);
        int height = _height >= _width ? 128 : Math.Max(1, _height * 128 / _width);
        var pixels = new byte[width * height * 4]; IntPtr buffer = GetBuffer(sample); bool locked = false;
        try
        {
            IntPtr data; uint max, length;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length)); locked = true;
            long stride = Math.Abs((long)_stride);
            if (length < stride * (_height - 1) + _width * 4L) throw new InvalidDataException("视频帧缓冲区不完整");
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int sy = Math.Min(_height - 1, (int)((y + .5) * _height / height));
                int sx = Math.Min(_width - 1, (int)((x + .5) * _width / width));
                long offset = (_stride < 0 ? _height - 1 - sy : sy) * stride + sx * 4L;
                uint color = *(uint*)((byte*)data + offset); int dest = (y * width + x) * 4;
                pixels[dest] = (byte)color; pixels[dest + 1] = (byte)(color >> 8); pixels[dest + 2] = (byte)(color >> 16); pixels[dest + 3] = 255;
            }
            return (pixels, width, height);
        }
        finally { if (locked) ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); Release(ref buffer); }
    }

    internal MediaFingerprint ReadAudio(string path, CancellationToken token)
    {
        const int outputRate = MediaAudioFingerprint.SampleRate;
        var pcm = new List<double>(outputRate * 120); double nextPosition = 0, previous = 0;
        long inputIndex = 0; double step = (double)_rate / outputRate;
        int reads = 0;
        while (pcm.Count < outputRate * 120)
        {
            if (++reads > 100_000) throw new InvalidDataException("音频解码进度异常，已停止此文件");
            token.ThrowIfCancellationRequested(); var sample = Read(out uint flags, out _);
            try
            {
                if ((flags & 0x20) != 0) throw new InvalidDataException("音频格式在文件中途发生变化");
                if (sample != IntPtr.Zero)
                {
                    IntPtr buffer = GetBuffer(sample); bool locked = false;
                    try
                    {
                        IntPtr data; uint max, length;
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length)); locked = true;
                        if (length > 32 * 1024 * 1024) throw new InvalidDataException("音频块超过安全上限");
                        int frames = (int)length / (2 * _channels);
                        for (int frame = 0; frame < frames && pcm.Count < outputRate * 120; frame++, inputIndex++)
                        {
                            double current = 0;
                            for (int channel = 0; channel < _channels; channel++) current += ((short*)data)[frame * _channels + channel] / 32768.0 / _channels;
                            while (nextPosition <= inputIndex && pcm.Count < outputRate * 120)
                            {
                                double fraction = Math.Clamp(nextPosition - (inputIndex - 1), 0, 1);
                                pcm.Add(previous * (1 - fraction) + current * fraction); nextPosition += step;
                            }
                            previous = current;
                        }
                    }
                    finally { if (locked) ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); Release(ref buffer); }
                }
                if ((flags & 2) != 0) break;
            }
            finally { Release(ref sample); }
        }
        if (pcm.Count < outputRate * 8) throw new InvalidDataException("音频不足 8 秒，未建立可靠声音指纹");
        var fingerprint = MediaAudioFingerprint.Create(pcm.ToArray(), token); var file = new FileInfo(path);
        return new() { Path = path, Kind = MediaKind.Audio, Length = file.Length, WrittenTicks = file.LastWriteTimeUtc.Ticks,
            Duration = Duration > 0 ? Duration : inputIndex / (double)_rate, AudioHashes = fingerprint.Hashes, AudioEnergy = fingerprint.Energy };
    }

    private IntPtr Read(out uint flags, out long timestamp)
    {
        uint actual = 0, nativeFlags = 0; long time = 0; IntPtr sample = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(_reader, 9))
            (_reader, _stream, 0, &actual, &nativeFlags, &time, &sample);
        if (hr < 0 || (nativeFlags & 1) != 0) { Release(ref sample); Check(hr < 0 ? hr : unchecked((int)0x80004005)); }
        flags = nativeFlags; timestamp = time; return sample;
    }
    private static IntPtr GetBuffer(IntPtr sample)
    {
        IntPtr buffer = IntPtr.Zero;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer)); return buffer;
    }
    private static void* Slot(IntPtr obj, int slot) => (*(void***)obj)[slot];
    private static void SetGuid(IntPtr obj, Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(obj, 24))(obj, &key, &value));
    private static void Set32(IntPtr obj, Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(obj, 21))(obj, &key, value));
    private static uint Get32(IntPtr obj, Guid key, uint? fallback = null)
    {
        uint value = 0; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(obj, 7))(obj, &key, &value);
        if (hr < 0 && fallback.HasValue) return fallback.Value;
        Check(hr); return value;
    }
    private static ulong Get64(IntPtr obj, Guid key)
    { ulong value = 0; Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(obj, 8))(obj, &key, &value)); return value; }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private static void Release(ref IntPtr pointer) { if (pointer != IntPtr.Zero) { Marshal.Release(pointer); pointer = IntPtr.Zero; } }
    public void Dispose() { Release(ref _reader); if (_started) { MFShutdown(); _started = false; } if (_uninitialize) { CoUninitialize(); _uninitialize = false; } }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public long Number; }
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, uint capacity);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MFCreateSourceReaderFromURL(string path, IntPtr attributes, out IntPtr reader);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
