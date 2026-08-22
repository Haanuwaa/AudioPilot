using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Services.UI.Interop
{
    internal static partial class ColorEmojiDWriteInterop
    {
        private static readonly Guid _factoryIid = new("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48");

        public static ColorEmojiComHandle<IColorEmojiDWriteFactory> CreateFactory()
        {
            Guid factoryIid = _factoryIid;
            int hresult = DWriteCreateFactory(ColorEmojiDWriteFactoryType.Shared, ref factoryIid, out IntPtr factoryPointer);
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            return ColorEmojiComRuntime.Wrap<IColorEmojiDWriteFactory>(factoryPointer);
        }

        [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
        private static partial int DWriteCreateFactory(ColorEmojiDWriteFactoryType factoryType, ref Guid iid, out IntPtr factory);
    }

    internal enum ColorEmojiDWriteFactoryType : uint
    {
        Shared = 0,
        Isolated = 1
    }

    internal enum ColorEmojiDWriteFontWeight : uint
    {
        Regular = 400
    }

    internal enum ColorEmojiDWriteFontStyle : uint
    {
        Normal = 0
    }

    internal enum ColorEmojiDWriteFontStretch : uint
    {
        Normal = 5
    }

    internal enum ColorEmojiDWriteTextAlignment : uint
    {
        Leading = 0
    }

    internal enum ColorEmojiDWriteParagraphAlignment : uint
    {
        Near = 0
    }

    internal enum ColorEmojiDWriteMeasuringMode : uint
    {
        Natural = 0
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiDWriteFactory
    {
        void GetSystemFontCollection();
        void CreateCustomFontCollection();
        void RegisterFontCollectionLoader();
        void UnregisterFontCollectionLoader();
        void CreateFontFileReference();
        void CreateCustomFontFileReference();
        void CreateFontFace();
        void CreateRenderingParams();
        void CreateMonitorRenderingParams();
        void CreateCustomRenderingParams();
        void RegisterFontFileLoader();
        void UnregisterFontFileLoader();
        void CreateTextFormat(string fontFamilyName, IntPtr fontCollection, ColorEmojiDWriteFontWeight fontWeight, ColorEmojiDWriteFontStyle fontStyle, ColorEmojiDWriteFontStretch fontStretch, float fontSize, string localeName, out IntPtr textFormat);
    }

    [GeneratedComInterface]
    [Guid("9C906818-31D7-4FD3-A151-7C5E225DB55A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiDWriteTextFormat
    {
        void SetTextAlignment(ColorEmojiDWriteTextAlignment textAlignment);
        void SetParagraphAlignment(ColorEmojiDWriteParagraphAlignment paragraphAlignment);
        void SetWordWrapping();
        void SetReadingDirection();
        void SetFlowDirection();
        void SetIncrementalTabStop();
        void SetTrimming();
        void SetLineSpacing();
    }
}
