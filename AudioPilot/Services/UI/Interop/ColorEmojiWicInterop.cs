using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Services.UI.Interop
{
    internal static class ColorEmojiWicInterop
    {
        public static readonly Guid ImagingFactoryClsid = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
        public static readonly Guid PixelFormat32bppPbgra = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

        public static ColorEmojiComHandle<IColorEmojiWicImagingFactory> CreateFactory()
        {
            return ColorEmojiComRuntime.CoCreate<IColorEmojiWicImagingFactory>(ImagingFactoryClsid);
        }
    }

    internal enum ColorEmojiWicBitmapCreateCacheOption : uint
    {
        NoCache = 0,
        CacheOnDemand = 1,
        CacheOnLoad = 2
    }

    [GeneratedComInterface]
    [Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiWicImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        void CreateFormatConverter();
        void CreateBitmapScaler();
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        void CreateStream();
        void CreateColorContext();
        void CreateColorTransformer();
        void CreateBitmap(uint uiWidth, uint uiHeight, ref Guid pixelFormat, ColorEmojiWicBitmapCreateCacheOption option, out IntPtr bitmap);
    }

    [GeneratedComInterface]
    [Guid("00000120-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiWicBitmapSource
    {
        void GetSize();
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
    }

    [GeneratedComInterface]
    [Guid("00000121-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiWicBitmap : IColorEmojiWicBitmapSource
    {
        new void GetSize();
        new void GetPixelFormat();
        new void GetResolution();
        new void CopyPalette();
        new void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
        void Lock();
        void SetPalette();
        void SetResolution();
    }
}
