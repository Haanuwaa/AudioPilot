using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]
[assembly: InternalsVisibleTo("AudioPilot.CliHost")]
[assembly: InternalsVisibleTo("AudioPilot.Cli")]
[assembly: InternalsVisibleTo("AudioPilot.Tests")]
