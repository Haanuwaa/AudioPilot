using AudioPilot.Cli;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal const string CliJsonSchemaVersion = CliOutputFormatter.JsonSchemaVersion;

        public static string SerializeCliJson<T>(T data)
        {
            return CliOutputFormatter.SerializeCliJson(data);
        }
    }
}
