using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// D3D11・DXGI の COM の関数のうち、デコードした画像を Vulkan に渡すのに要るものだけを、関数表（vtable）の番号で呼ぶ。
/// 番号は Windows SDK の d3d11.h・dxgi.h・dxgi1_2.h の宣言の順（IUnknown の 3 つから数える）
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class D3D11Interop
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    // DXGI_FORMAT
    public const uint FormatNv12 = 103;
    public const uint FormatP010 = 104;

    // D3D11_RESOURCE_MISC_FLAG・D3D11_BIND_FLAG
    public const uint MiscShared = 0x2;
    public const uint MiscSharedNtHandle = 0x800;
    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;

    // DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE
    public const uint SharedResourceReadWrite = 0x80000001;

    public static readonly Guid IidDxgiResource1 = new("30961379-4609-4a41-998e-54fe567ee0c1");
    public static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static void** Table(nint self) => *(void***)self;

    // IUnknown
    public static int QueryInterface(nint self, Guid iid, out nint result)
    {
        nint value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(self)[0])(self, &iid, &value);
        result = value;
        return hr;
    }

    public static uint Release(nint self) => ((delegate* unmanaged[Stdcall]<nint, uint>)Table(self)[2])(self);

    // ID3D11Device
    public static int CreateTexture2D(nint device, in Texture2DDesc desc, out nint texture)
    {
        nint value;
        fixed (Texture2DDesc* pointer = &desc)
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)Table(device)[5])(device, pointer, null, &value);
            texture = value;
            return hr;
        }
    }

    public static int CreateQuery(nint device, uint query, out nint result)
    {
        var desc = stackalloc uint[2] { query, 0 };
        nint value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Table(device)[24])(device, desc, &value);
        result = value;
        return hr;
    }

    // ID3D11DeviceContext
    public static void End(nint context, nint query) => ((delegate* unmanaged[Stdcall]<nint, nint, void>)Table(context)[28])(context, query);

    public static int GetData(nint context, nint query, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)Table(context)[29])(context, query, null, 0, flags);

    public static void CopySubresourceRegion(nint context, nint destination, nint source, uint sourceSubresource) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)Table(context)[46])(
            context, destination, 0, 0, 0, 0, source, sourceSubresource, null);

    public static void Flush(nint context) => ((delegate* unmanaged[Stdcall]<nint, void>)Table(context)[111])(context);

    // ID3D11Texture2D
    public static Texture2DDesc GetDesc(nint texture)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)Table(texture)[10])(texture, &desc);
        return desc;
    }

    // IDXGIResource1
    public static int CreateSharedHandle(nint resource, out nint handle)
    {
        nint value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, void*, uint, char*, nint*, int>)Table(resource)[13])(resource, null, SharedResourceReadWrite, null, &value);
        handle = value;
        return hr;
    }

    // IDXGIDevice
    public static int GetAdapter(nint device, out nint adapter)
    {
        nint value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(device)[7])(device, &value);
        adapter = value;
        return hr;
    }

    // IDXGIAdapter。DXGI_ADAPTER_DESC（304 バイト）の AdapterLuid は 296 バイト目から
    public static byte[]? AdapterLuid(nint adapter)
    {
        var desc = stackalloc byte[304];
        if (((delegate* unmanaged[Stdcall]<nint, byte*, int>)Table(adapter)[8])(adapter, desc) != S_OK)
        {
            return null;
        }

        return new ReadOnlySpan<byte>(desc + 296, 8).ToArray();
    }

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);

    /// <summary>D3D11_TEXTURE2D_DESC</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }
}
