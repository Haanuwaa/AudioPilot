using System.Text;
using System.Text.Json;
using AudioPilot.Platform;
using BenchmarkDotNet.Attributes;

namespace AudioPilot.Benchmarks;

[MemoryDiagnoser]
public class CodecBenchmarks
{
    private SingleInstanceCommandResult _response;
    private byte[] _encoded = [];

    [Params(64, 256 * 1024, 1024 * 1024)]
    public int BodyWireBytes { get; set; }

    [Params("Ascii", "Unicode", "Escaped")]
    public string Content { get; set; } = "Ascii";

    [GlobalSetup]
    public void SetUp()
    {
        string pattern = Content switch { "Ascii" => "x", "Unicode" => "\u6F22", "Escaped" => "\"\n\\", _ => throw new InvalidOperationException("Unknown codec content.") };
        int encodedPatternBytes = JsonSerializer.SerializeToUtf8Bytes(pattern).Length - 2;
        string body = string.Concat(Enumerable.Repeat(pattern, BodyWireBytes / encodedPatternBytes))
            + new string('x', BodyWireBytes % encodedPatternBytes);
        _response = new(0, body, ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion);
        _encoded = SingleInstanceCommandResultParser.Serialize(_response);
        if (_encoded.Length > 4 * 1024 * 1024 || JsonSerializer.SerializeToUtf8Bytes(body).Length - 2 != BodyWireBytes ||
            !SingleInstanceCommandResultParser.TryParse(_encoded, out var decoded) || decoded.Response != _response)
            throw new InvalidOperationException("IPC benchmark response failed to round-trip.");
    }

    [Benchmark]
    public byte[] Encode() => SingleInstanceCommandResultParser.Serialize(_response);

    [Benchmark]
    public SingleInstanceCommandResult Decode() =>
        SingleInstanceCommandResultParser.TryParse(_encoded, out var decoded)
            ? decoded.Response
            : throw new InvalidOperationException("IPC benchmark response could not be decoded.");

    internal static void WriteCaseManifest(string outputDirectory)
    {
        var cases = new List<object>();
        foreach (string content in new[] { "Ascii", "Unicode", "Escaped" })
            foreach (int budget in new[] { 64, 256 * 1024, 1024 * 1024 })
            {
                var benchmark = new CodecBenchmarks { Content = content, BodyWireBytes = budget };
                benchmark.SetUp();
                cases.Add(new
                {
                    Content = content,
                    BodyWireBytes = budget,
                    OutputCharacters = benchmark._response.Output!.Length,
                    OutputUtf8Bytes = Encoding.UTF8.GetByteCount(benchmark._response.Output),
                    EncodedBytes = benchmark._encoded.Length
                });
            }
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "codec-cases.json"), JsonSerializer.Serialize(cases, IpcBenchmarkSession.ReportJson));
    }
}
