using System.Runtime.InteropServices;

namespace AudioPilot.Updater;

internal static partial class MsiDeployment
{
    private const string UpgradeCode = "{8D18A00C-7CC8-487B-A462-7D3252139D72}";

    internal static unsafe IReadOnlyCollection<string> ReadRepairSourceDirectories()
    {
        var products = new List<string>();
        char* product = stackalloc char[39];
        for (uint index = 0; ; index++)
        {
            if (index == 128) throw new InvalidDataException("Too many related installer products.");
            uint result = MsiEnumRelatedProducts(UpgradeCode, 0, index, product);
            if (result == 259) break;
            Check(result, "enumerate repair products");
            products.Add(new string(product));
        }
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        char[] buffer = new char[32768];
        foreach (string code in products)
        {
            bool found = false;
            foreach (uint context in new uint[] { 1, 2, 4 })
            {
                for (uint index = 0; ; index++)
                {
                    if (index == 4096) throw new InvalidDataException("Too many installer repair sources.");
                    uint length = (uint)buffer.Length;
                    uint result;
                    fixed (char* destination = buffer)
                        result = MsiSourceListEnumSources(code, null, context, 1, index, destination, ref length);
                    if (result == 1605 && index == 0) break;
                    if (result == 259) { found = true; break; }
                    Check(result, "enumerate repair sources");
                    found = true;
                    sources.Add(new string(buffer, 0, checked((int)length)));
                }
            }
            if (!found) throw new InvalidDataException("An installed product's repair sources could not be read.");
        }
        return sources;
    }

    [LibraryImport("msi.dll", EntryPoint = "MsiSourceListEnumSourcesW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint MsiSourceListEnumSources(string product, string? sid, uint context, uint options, uint index, char* source, ref uint length);
}
