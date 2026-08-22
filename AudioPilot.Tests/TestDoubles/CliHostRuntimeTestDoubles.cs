using System.Text.Json;
using System.Text.Json.Serialization;
using AudioPilot.Cli;
using AudioPilot.CliHost;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeCliHostSingleInstance : ICliHostSingleInstance
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool TryAcquireResult { get; set; } = true;
    public bool LastSignalExistingSucceededValue { get; set; }
    public SingleInstanceSignalFailureKind LastSignalFailureKindValue { get; set; }
    public int? LastSignalExitCodeValue { get; set; }
    public string? LastSignalOutputValue { get; set; }
    public string? LastSignalErrorCodeValue { get; set; }
    public string? LastSignalErrorMessageValue { get; set; }
    public int? LastSignalProtocolVersionValue { get; set; }
    public string? ReceivedPayload { get; private set; }

    public bool LastSignalExistingSucceeded => LastSignalExistingSucceededValue;
    public SingleInstanceSignalFailureKind LastSignalFailureKind => LastSignalFailureKindValue;
    public int? LastSignalExitCode => NormalizeResponse().ExitCode;
    public string? LastSignalOutput => NormalizeResponse().Output;
    public string? LastSignalErrorCode => NormalizeResponse().ErrorCode;
    public string? LastSignalErrorMessage => NormalizeResponse().ErrorMessage;
    public int? LastSignalProtocolVersion => NormalizeResponse().ProtocolVersion;

    public bool TryAcquire(string? payloadToExistingInstance = null)
    {
        ReceivedPayload = payloadToExistingInstance;
        return TryAcquireResult;
    }

    public void Dispose()
    {
    }

    private SingleInstanceCommandResult NormalizeResponse()
    {
        string payload = JsonSerializer.Serialize(
            new SingleInstanceCommandResult(
                LastSignalExitCodeValue ?? 0,
                LastSignalOutputValue,
                LastSignalErrorCodeValue,
                LastSignalErrorMessageValue,
                LastSignalProtocolVersionValue),
            SerializerOptions);

        return SingleInstanceCommandResultParser.TryParse(payload, out SingleInstanceCommandResultParseResult parsed)
            ? parsed.Response
            : new SingleInstanceCommandResult(LastSignalExitCodeValue ?? 0, LastSignalOutputValue, LastSignalErrorCodeValue, LastSignalErrorMessageValue, LastSignalProtocolVersionValue);
    }
}

internal sealed class FakeCliHeadlessRunner : ICliHeadlessCommandRunner
{
    public CliExecutionResult Result { get; set; } = new(0, string.Empty);
    public Exception? ExceptionToThrow { get; set; }
    public CliCommand? ReceivedCommand { get; private set; }

    public CliExecutionResult Execute(CliCommand command)
    {
        ReceivedCommand = command;
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        return Result;
    }

    public void Dispose()
    {
    }
}
