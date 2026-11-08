using System.Diagnostics;
using System.Runtime.Versioning;
using FFmpeg.AutoGen.Abstractions;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// Windows のハードウェアデコード（FFmpeg の d3d11va）のデバイスと、ほかの API（Vulkan）と共有できる 1 枚ずつのテクスチャ。
/// FFmpeg はデコードした画像を 1 つのテクスチャ配列に入れる（配列はデバイスの間で共有しにくい）ので、
/// デコードした 1 枚を、GPU の上で、共有できるテクスチャ（D3D11SharedTexture）に写して渡す（メインメモリへは写さない）。
/// 共有のテクスチャは、大きさと形式ごとに MaxTextures 枚まで作って使い回す（足りなければ CopyFrame は null を返す）
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class D3D11VideoDevice : IDisposable
{
    /// <summary>大きさと形式ごとの共有のテクスチャの最大の数（デコーダーが覚えているフレームと、描画が使っている途中のフレームの分）</summary>
    public const int MaxTextures = 32;

    // D3D11_QUERY_EVENT・D3D11_ASYNC_GETDATA_DONOTFLUSH
    private const uint QueryEvent = 0;
    private const uint GetDataDoNotFlush = 1;

    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly List<D3D11SharedTexture> _textures = [];
    private AVBufferRef* _reference;
    private nint _query;
    private int _nextId;

    private D3D11VideoDevice(AVBufferRef* reference, nint query)
    {
        _reference = reference;
        _query = query;
    }

    /// <summary>FFmpeg のデバイス（AVBufferRef*）。VideoDecoderOptions.D3D11 に渡すと、デコーダーがこのデバイスでデコードする</summary>
    public nint Reference => (nint)_reference;

    private AVD3D11VADeviceContext* Context => (AVD3D11VADeviceContext*)((AVHWDeviceContext*)_reference->data)->hwctx;

    /// <summary>
    /// d3d11va のデバイスを作る。luid を指定すれば、そのアダプター（描画に使う GPU）を探して使う。作れなければ null と理由
    /// </summary>
    public static D3D11VideoDevice? TryCreate(byte[]? luid, out string? problem)
    {
        if (!FfmpegLibrary.TryLoad(out _, out var error))
        {
            problem = error;
            return null;
        }

        var type = ffmpeg.av_hwdevice_find_type_by_name("d3d11va");
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            problem = "FFmpeg が D3D11 のデコードに対応していません";
            return null;
        }

        // アダプターの番号を順に試し、描画と同じ GPU（LUID が同じ）を使う
        for (var adapter = 0; adapter < (luid is null ? 1 : 4); adapter++)
        {
            AVBufferRef* reference = null;
            if (ffmpeg.av_hwdevice_ctx_create(&reference, type, adapter.ToString(System.Globalization.CultureInfo.InvariantCulture), null, 0) < 0)
            {
                break;
            }

            var hw = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)reference->data)->hwctx;
            if (luid is not null && !luid.AsSpan().SequenceEqual(AdapterLuid((nint)hw->device)))
            {
                ffmpeg.av_buffer_unref(&reference);
                continue;
            }

            if (D3D11Interop.CreateQuery((nint)hw->device, QueryEvent, out var query) != D3D11Interop.S_OK)
            {
                ffmpeg.av_buffer_unref(&reference);
                problem = "D3D11 の待ち合わせ（クエリー）を作れません";
                return null;
            }

            problem = null;
            return new D3D11VideoDevice(reference, query);
        }

        problem = "描画に使う GPU で、D3D11 のデコードを使えません";
        return null;
    }

    // デバイスのアダプターの LUID
    private static byte[] AdapterLuid(nint device)
    {
        if (D3D11Interop.QueryInterface(device, D3D11Interop.IidDxgiDevice, out var dxgi) != D3D11Interop.S_OK)
        {
            return [];
        }

        try
        {
            if (D3D11Interop.GetAdapter(dxgi, out var adapter) != D3D11Interop.S_OK)
            {
                return [];
            }

            try
            {
                return D3D11Interop.AdapterLuid(adapter) ?? [];
            }
            finally
            {
                D3D11Interop.Release(adapter);
            }
        }
        finally
        {
            D3D11Interop.Release(dxgi);
        }
    }

    /// <summary>
    /// d3d11va でデコードしたフレーム（AV_PIX_FMT_D3D11）を、共有のテクスチャに写し、写し終わるまで待つ。
    /// 空いている共有のテクスチャがない・作れないときは null（呼ぶ側はメインメモリに写す）。返したテクスチャは借りている状態なので、使い終わったら Return する
    /// </summary>
    internal D3D11SharedTexture? CopyFrame(AVFrame* frame, VideoPlaneFormat format)
    {
        var source = (nint)frame->data[0];
        var index = (uint)(nint)frame->data[1];
        var desc = D3D11Interop.GetDesc(source);
        var target = Rent(desc, format);
        if (target is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                // FFmpeg も同じ ID3D11DeviceContext を使うので、FFmpeg の錠を取ってから使う
                var hw = Context;
                Lock(hw);
                try
                {
                    D3D11Interop.CopySubresourceRegion((nint)hw->device_context, target.Texture, source, index);
                    D3D11Interop.End((nint)hw->device_context, _query);
                    D3D11Interop.Flush((nint)hw->device_context);
                }
                finally
                {
                    Unlock(hw);
                }

                // 写し終わるまで待つ（Vulkan が読むのはその後）
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    int result;
                    Lock(hw);
                    try
                    {
                        result = D3D11Interop.GetData((nint)hw->device_context, _query, GetDataDoNotFlush);
                    }
                    finally
                    {
                        Unlock(hw);
                    }

                    if (result == D3D11Interop.S_OK)
                    {
                        break;
                    }

                    if (result != D3D11Interop.S_FALSE || watch.Elapsed > CopyTimeout)
                    {
                        throw new MediaException("デコードした映像を、描画の GPU に渡せませんでした");
                    }

                    Thread.SpinWait(64);
                }
            }

            return target;
        }
        catch
        {
            target.Return();
            throw;
        }
    }

    // 同じ大きさと形式の、空いている共有のテクスチャを借りる。なければ作る（MaxTextures 枚まで）
    private D3D11SharedTexture? Rent(D3D11Interop.Texture2DDesc source, VideoPlaneFormat format)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_reference is null, this);
            var count = 0;
            foreach (var texture in _textures)
            {
                if (texture.Width == source.Width && texture.Height == source.Height && texture.Format == format)
                {
                    count++;
                    if (texture.TryRent())
                    {
                        return texture;
                    }
                }
            }

            if (count >= MaxTextures || CreateShared(source, format) is not { } created)
            {
                return null;
            }

            _textures.Add(created);
            created.TryRent();
            return created;
        }
    }

    private D3D11SharedTexture? CreateShared(D3D11Interop.Texture2DDesc source, VideoPlaneFormat format)
    {
        var desc = new D3D11Interop.Texture2DDesc
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleCount = 1,
            BindFlags = D3D11Interop.BindShaderResource,
            MiscFlags = D3D11Interop.MiscShared | D3D11Interop.MiscSharedNtHandle,
        };
        var device = (nint)Context->device;
        if (D3D11Interop.CreateTexture2D(device, desc, out var texture) != D3D11Interop.S_OK)
        {
            desc.BindFlags = D3D11Interop.BindShaderResource | D3D11Interop.BindRenderTarget;
            if (D3D11Interop.CreateTexture2D(device, desc, out texture) != D3D11Interop.S_OK)
            {
                return null;
            }
        }

        if (D3D11Interop.QueryInterface(texture, D3D11Interop.IidDxgiResource1, out var resource) != D3D11Interop.S_OK)
        {
            D3D11Interop.Release(texture);
            return null;
        }

        try
        {
            if (D3D11Interop.CreateSharedHandle(resource, out var handle) != D3D11Interop.S_OK)
            {
                D3D11Interop.Release(texture);
                return null;
            }

            return new D3D11SharedTexture(++_nextId, texture, handle, (int)source.Width, (int)source.Height, format);
        }
        finally
        {
            D3D11Interop.Release(resource);
        }
    }

    private static void Lock(AVD3D11VADeviceContext* hw)
    {
        if (hw->@lock.Pointer != 0)
        {
            ((delegate* unmanaged[Cdecl]<void*, void>)hw->@lock.Pointer)(hw->lock_ctx);
        }
    }

    private static void Unlock(AVD3D11VADeviceContext* hw)
    {
        if (hw->unlock.Pointer != 0)
        {
            ((delegate* unmanaged[Cdecl]<void*, void>)hw->unlock.Pointer)(hw->lock_ctx);
        }
    }

    /// <summary>
    /// デバイスと共有のテクスチャを捨てる。共有のテクスチャを取り込んだもの（Vulkan の画像）と、このデバイスでデコードしているデコーダーは、先に捨てておくこと
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_reference is null)
            {
                return;
            }

            foreach (var texture in _textures)
            {
                D3D11Interop.CloseHandle(texture.Handle);
                D3D11Interop.Release(texture.Texture);
            }

            _textures.Clear();
            D3D11Interop.Release(_query);
            _query = 0;
            var reference = _reference;
            ffmpeg.av_buffer_unref(&reference);
            _reference = null;
        }
    }
}

/// <summary>
/// ほかの API と共有できる 1 枚のテクスチャ（NV12・P010）。Handle は NT ハンドル（Vulkan は VK_KHR_external_memory_win32 で取り込む）。
/// Width・Height はテクスチャの大きさ（デコードした映像より大きいことがある）。Id は取り込んだ画像を覚えておくためのキー。
/// 借りている間（Rent から Return まで）は、ほかのフレームに使わない
/// </summary>
public sealed class D3D11SharedTexture
{
    private int _leases;

    internal D3D11SharedTexture(int id, nint texture, nint handle, int width, int height, VideoPlaneFormat format)
    {
        Id = id;
        Texture = texture;
        Handle = handle;
        Width = width;
        Height = height;
        Format = format;
    }

    public int Id { get; }

    public nint Handle { get; }

    public int Width { get; }

    public int Height { get; }

    public VideoPlaneFormat Format { get; }

    internal nint Texture { get; }

    // 空いていれば借りる
    internal bool TryRent() => Interlocked.CompareExchange(ref _leases, 1, 0) == 0;

    /// <summary>同じフレームを、別の場所でも使う（借りる数を増やす）</summary>
    internal void AddLease() => Interlocked.Increment(ref _leases);

    /// <summary>使い終わった（借りる数が 0 になれば、ほかのフレームに使える）</summary>
    internal void Return()
    {
        if (Interlocked.Decrement(ref _leases) < 0)
        {
            Interlocked.Exchange(ref _leases, 0);
        }
    }
}
