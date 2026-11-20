using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AudioPilot.Services.UI
{
    internal static class AppIconImageProvider
    {
        private const string ResourceUri = "pack://application:,,,/AudioPilot;component/Images/audiopilot.ico";

        internal static Stream OpenIconStream()
        {
            RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
            return Application.GetResourceStream(new Uri(ResourceUri, UriKind.Absolute))?.Stream
                ?? throw new InvalidDataException("Embedded application icon is unavailable.");
        }
    }
}
