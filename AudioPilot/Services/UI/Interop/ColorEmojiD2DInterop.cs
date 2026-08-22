using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Services.UI.Interop
{
    /// <summary>
    /// Holds the generated-COM Direct2D contracts used by the color emoji renderer.
    /// The draw-path methods that proved runtime-sensitive stay on explicit vtable helpers
    /// because the fully source-generated signatures were not stable for this renderer.
    /// </summary>
    internal static partial class ColorEmojiD2DInterop
    {
        private static readonly Guid _factoryIid = new("06152247-6F50-465A-9245-118BFD3B6007");
        private const int CreateSolidColorBrushVtableIndex = 8;
        private const int DrawTextVtableIndex = 27;
        private const int ClearVtableIndex = 47;
        private const int BeginDrawVtableIndex = 48;
        private const int EndDrawVtableIndex = 49;

        public static ColorEmojiComHandle<IColorEmojiD2DFactory> CreateFactory()
        {
            Guid factoryIid = _factoryIid;
            int hresult = D2D1CreateFactory(ColorEmojiD2DFactoryType.SingleThreaded, ref factoryIid, IntPtr.Zero, out IntPtr factoryPointer);
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            return ColorEmojiComRuntime.Wrap<IColorEmojiD2DFactory>(factoryPointer);
        }

        [LibraryImport("d2d1.dll", EntryPoint = "D2D1CreateFactory")]
        private static partial int D2D1CreateFactory(ColorEmojiD2DFactoryType factoryType, ref Guid riid, IntPtr factoryOptions, out IntPtr factory);

        /// <summary>
        /// Invokes <c>ID2D1RenderTarget::CreateSolidColorBrush</c> through the native vtable to
        /// preserve the stable signature shape that passed the incremental interop validation tests.
        /// </summary>
        public static unsafe IntPtr CreateSolidColorBrush(IntPtr renderTargetPointer, ref ColorEmojiD2DColorF color)
        {
            IntPtr brushPointer = IntPtr.Zero;
            IntPtr* vtable = *(IntPtr**)renderTargetPointer;
            delegate* unmanaged[Stdcall]<IntPtr, ColorEmojiD2DColorF*, IntPtr, IntPtr*, int> createSolidColorBrush =
                (delegate* unmanaged[Stdcall]<IntPtr, ColorEmojiD2DColorF*, IntPtr, IntPtr*, int>)vtable[CreateSolidColorBrushVtableIndex];
            ColorEmojiD2DColorF colorValue = color;

            int hresult = createSolidColorBrush(renderTargetPointer, &colorValue, IntPtr.Zero, &brushPointer);
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            return brushPointer;
        }

        /// <summary>
        /// Invokes <c>ID2D1RenderTarget::DrawText</c> through the native vtable so color-font emoji
        /// rendering keeps the verified ABI rather than relying on the source generator here.
        /// </summary>
        public static unsafe void DrawText(
            IntPtr renderTargetPointer,
            string text,
            IntPtr textFormatPointer,
            ref ColorEmojiD2DRectF layoutRect,
            IntPtr brushPointer,
            ColorEmojiD2DDrawTextOptions options,
            ColorEmojiDWriteMeasuringMode measuringMode)
        {
            IntPtr* vtable = *(IntPtr**)renderTargetPointer;
            delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, ColorEmojiD2DRectF*, IntPtr, ColorEmojiD2DDrawTextOptions, ColorEmojiDWriteMeasuringMode, void> drawText =
                (delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, ColorEmojiD2DRectF*, IntPtr, ColorEmojiD2DDrawTextOptions, ColorEmojiDWriteMeasuringMode, void>)vtable[DrawTextVtableIndex];
            ColorEmojiD2DRectF layoutRectValue = layoutRect;

            fixed (char* textPointer = text)
            {
                drawText(renderTargetPointer, textPointer, (uint)text.Length, textFormatPointer, &layoutRectValue, brushPointer, options, measuringMode);
            }
        }

        /// <summary>
        /// Invokes <c>ID2D1RenderTarget::Clear</c> through the native vtable so the color struct
        /// follows the same verified ABI as the rest of the explicit Direct2D draw path.
        /// </summary>
        public static unsafe void Clear(IntPtr renderTargetPointer, ref ColorEmojiD2DColorF clearColor)
        {
            IntPtr* vtable = *(IntPtr**)renderTargetPointer;
            delegate* unmanaged[Stdcall]<IntPtr, ColorEmojiD2DColorF*, void> clear =
                (delegate* unmanaged[Stdcall]<IntPtr, ColorEmojiD2DColorF*, void>)vtable[ClearVtableIndex];
            ColorEmojiD2DColorF clearColorValue = clearColor;
            clear(renderTargetPointer, &clearColorValue);
        }

        /// <summary>
        /// Invokes <c>ID2D1RenderTarget::BeginDraw</c> through the native vtable to preserve the
        /// verified ABI shape for Direct2D draw setup across Debug and Release builds.
        /// </summary>
        public static unsafe void BeginDraw(IntPtr renderTargetPointer)
        {
            IntPtr* vtable = *(IntPtr**)renderTargetPointer;
            delegate* unmanaged[Stdcall]<IntPtr, void> beginDraw =
                (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[BeginDrawVtableIndex];
            beginDraw(renderTargetPointer);
        }

        /// <summary>
        /// Invokes <c>ID2D1RenderTarget::EndDraw</c> through the native vtable so HRESULT handling
        /// follows the ABI that passed incremental validation for this renderer.
        /// </summary>
        public static unsafe void EndDraw(IntPtr renderTargetPointer, out ulong tag1, out ulong tag2)
        {
            IntPtr* vtable = *(IntPtr**)renderTargetPointer;
            delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int> endDraw =
                (delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)vtable[EndDrawVtableIndex];

            ulong localTag1 = 0;
            ulong localTag2 = 0;
            int hresult = endDraw(renderTargetPointer, &localTag1, &localTag2);
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            tag1 = localTag1;
            tag2 = localTag2;
        }
    }

    internal enum ColorEmojiD2DFactoryType : uint
    {
        SingleThreaded = 0,
        MultiThreaded = 1
    }

    internal enum ColorEmojiD2DRenderTargetType : uint
    {
        Default = 0,
        Software = 1,
        Hardware = 2
    }

    internal enum ColorEmojiDxgiFormat : uint
    {
        Unknown = 0
    }

    internal enum ColorEmojiD2DAlphaMode : uint
    {
        Unknown = 0
    }

    internal enum ColorEmojiD2DRenderTargetUsage : uint
    {
        None = 0
    }

    internal enum ColorEmojiD2DFeatureLevel : uint
    {
        Default = 0
    }

    [Flags]
    internal enum ColorEmojiD2DDrawTextOptions : uint
    {
        None = 0,
        NoSnap = 1,
        Clip = 2,
        EnableColorFont = 4,
        DisableColorBitmapSnapping = 8
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorEmojiD2DPixelFormat
    {
        public ColorEmojiDxgiFormat Format;
        public ColorEmojiD2DAlphaMode AlphaMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorEmojiD2DRenderTargetProperties
    {
        public ColorEmojiD2DRenderTargetType Type;
        public ColorEmojiD2DPixelFormat PixelFormat;
        public float DpiX;
        public float DpiY;
        public ColorEmojiD2DRenderTargetUsage Usage;
        public ColorEmojiD2DFeatureLevel MinLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorEmojiD2DColorF(float r, float g, float b, float a)
    {
        public static readonly ColorEmojiD2DColorF Transparent = new(0f, 0f, 0f, 0f);
        public static readonly ColorEmojiD2DColorF Black = new(0f, 0f, 0f, 1f);

        public float R = r;
        public float G = g;
        public float B = b;
        public float A = a;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorEmojiD2DRectF(float left, float top, float right, float bottom)
    {
        public float Left = left;
        public float Top = top;
        public float Right = right;
        public float Bottom = bottom;
    }

    [GeneratedComInterface]
    [Guid("06152247-6F50-465A-9245-118BFD3B6007")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiD2DFactory
    {
        void ReloadSystemMetrics();
        void GetDesktopDpi();
        void CreateRectangleGeometry();
        void CreateRoundedRectangleGeometry();
        void CreateEllipseGeometry();
        void CreateGeometryGroup();
        void CreateTransformedGeometry();
        void CreatePathGeometry();
        void CreateStrokeStyle();
        void CreateDrawingStateBlock();
        void CreateWicBitmapRenderTarget(IntPtr target, ref ColorEmojiD2DRenderTargetProperties renderTargetProperties, out IntPtr renderTarget);
    }

    [GeneratedComInterface]
    [Guid("2CD90694-12E2-11DC-9FED-001143A055F9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IColorEmojiD2DRenderTarget
    {
        void GetFactory();
        void CreateBitmap();
        void CreateBitmapFromWicBitmap();
        void CreateSharedBitmap();
        void CreateBitmapBrush();
        void CreateSolidColorBrush();
        void CreateGradientStopCollection();
        void CreateLinearGradientBrush();
        void CreateRadialGradientBrush();
        void CreateCompatibleRenderTarget();
        void CreateLayer();
        void CreateMesh();
        void DrawLine();
        void DrawRectangle();
        void FillRectangle();
        void DrawRoundedRectangle();
        void FillRoundedRectangle();
        void DrawEllipse();
        void FillEllipse();
        void DrawGeometry();
        void FillGeometry();
        void FillMesh();
        void FillOpacityMask();
        void DrawBitmap();
        void DrawText();
        void DrawTextLayout();
        void DrawGlyphRun();
        void SetTransform();
        void GetTransform();
        void SetAntialiasMode();
        void GetAntialiasMode();
        void SetTextAntialiasMode();
        void GetTextAntialiasMode();
        void SetTextRenderingParams();
        void GetTextRenderingParams();
        void SetTags();
        void GetTags();
        void PushLayer();
        void PopLayer();
        void Flush();
        void SaveDrawingState();
        void RestoreDrawingState();
        void PushAxisAlignedClip();
        void PopAxisAlignedClip();
        void Clear(ref ColorEmojiD2DColorF clearColor);
        void BeginDraw();
        void EndDraw(out ulong tag1, out ulong tag2);
    }
}
