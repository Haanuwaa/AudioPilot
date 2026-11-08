using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using AudioPilot.Constants;

namespace AudioPilot.Platform;

internal enum SingleInstanceOwnerState { Unknown, Responsive, Starting, Stopping, Headless, Unresponsive }

internal readonly record struct SingleInstanceOwnerIdentity(int ProcessId, long StartTimeUtcTicks);

internal readonly record struct SingleInstanceOwnerProbe(SingleInstanceOwnerState State, SingleInstanceOwnerIdentity? Identity = null);

public partial class SingleInstanceHelper
{
    private const string OwnerProbePayload = "PROBE_UI";
    private const string OwnerResponsive = "owner-ui-responsive";
    private const string OwnerStarting = "owner-ui-starting";
    private const string OwnerStopping = "owner-ui-stopping";
    private const string OwnerUnresponsive = "owner-ui-unresponsive";
    internal event Func<CancellationToken, Task<SingleInstanceOwnerState>>? OwnerStateRequested;

    internal async Task<SingleInstanceOwnerProbe> ProbeExistingOwnerAsync(CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", _activationPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        using var deadline = new PipeDeadline(AppConstants.Timing.SingleInstanceOwnerProbeTimeoutMs, _timeProvider, cancellationToken);
        try
        {
            await client.ConnectAsync(AppConstants.Timing.SingleInstanceConnectTimeoutMs, deadline.Token).ConfigureAwait(false);
            // The scoped listener belongs to the mutex holder. Get its PID from Windows before trusting the reply.
            _ = GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint serverId);
            using Process? peer = OpenProbePeer(serverId);
            await WriteFramedPayloadAsync(client, Encoding.UTF8.GetBytes(OwnerProbePayload), deadline.Token).ConfigureAwait(false);
            byte[]? payload = await ReadFramedPayloadAsync(client, MaxRequestBytes, deadline.Token).ConfigureAwait(false);
            if (payload == null || !SingleInstanceCommandResultParser.TryParse(payload, out var parsed)
                || parsed.Response.ProtocolVersion != ResponseProtocolVersion)
                return default;

            SingleInstanceOwnerState state = parsed.Response.ErrorCode switch
            {
                OwnerResponsive => SingleInstanceOwnerState.Responsive,
                OwnerStarting => SingleInstanceOwnerState.Starting,
                OwnerStopping => SingleInstanceOwnerState.Stopping,
                HeadlessBusyErrorCode => SingleInstanceOwnerState.Headless,
                OwnerUnresponsive => SingleInstanceOwnerState.Unresponsive,
                _ => SingleInstanceOwnerState.Unknown,
            };
            SingleInstanceOwnerIdentity? identity = state == SingleInstanceOwnerState.Unresponsive && peer is { HasExited: false }
                ? new(peer.Id, peer.StartTime.ToUniversalTime().Ticks)
                : null;
            return new(state, identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Debug("SingleInstanceHelper", () => $"owner-probe-unavailable | error={ex.GetType().Name}");
            return default;
        }
    }

    private static Process? OpenProbePeer(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return null;
        Process? peer = null;
        try
        {
            peer = Process.GetProcessById((int)processId);
            _ = peer.SafeHandle;
            return peer;
        }
        catch (Exception)
        {
            peer?.Dispose();
            return null;
        }
    }

    private async Task RespondToOwnerProbeAsync(NamedPipeServerStream server, CancellationToken shutdownToken)
    {
        using var deadline = new PipeDeadline(AppConstants.Timing.SingleInstanceOwnerProbeTimeoutMs, _timeProvider, shutdownToken);
        SingleInstanceCommandResult response = HeadlessBusyResponse;
        if (!_isHeadless)
        {
            var handler = OwnerStateRequested;
            SingleInstanceOwnerState state = handler == null ? SingleInstanceOwnerState.Starting : await handler(deadline.Token).ConfigureAwait(false);
            string? code = state switch
            {
                SingleInstanceOwnerState.Responsive => OwnerResponsive,
                SingleInstanceOwnerState.Starting => OwnerStarting,
                SingleInstanceOwnerState.Stopping => OwnerStopping,
                SingleInstanceOwnerState.Unresponsive => OwnerUnresponsive,
                _ => null,
            };
            response = new(0, ErrorCode: code, ProtocolVersion: ResponseProtocolVersion);
        }
        await WriteResponseAsync(server, response, deadline.Token).ConfigureAwait(false);
    }
}
