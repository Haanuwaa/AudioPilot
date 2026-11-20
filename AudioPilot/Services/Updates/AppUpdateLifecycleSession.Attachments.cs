using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Services.Updates;

internal sealed partial class AppUpdateLifecycleSession
{
    internal const string AttachmentEnvironment = "AUDIOPILOT_UPDATE_TEST_SESSION";
    private static string? _pilotAttachmentIdentity;
    internal static string? AttachmentSession
    {
        get
        {
            string? id = Environment.GetEnvironmentVariable(AttachmentEnvironment);
            if (id == null)
            {
                if (UpdateClientPolicy.Current?.IsEnabled != true) return null;
                if (_pilotAttachmentIdentity is { } known) return known;
                string candidate = Current?._launch.Pilot == true ? Current._launch.DiagnosticId : Guid.NewGuid().ToString("N");
                return Interlocked.CompareExchange(ref _pilotAttachmentIdentity, candidate, null) ?? candidate;
            }
            if (!Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(id, "N", out var value) || value == Guid.Empty)
                throw new InvalidDataException("Updater attachment tests are restricted to an explicit Sandbox session.");
            return id;
        }
    }
    internal static async Task ListenForAttachmentsAsync(AppRuntimeHost host, CancellationToken token)
    {
        string? session = AttachmentSession;
        if (session == null) return;
        using var self = Process.GetCurrentProcess();
        string name = UpdateLifecycleWire.AttachmentPipe(self.Id, self.StartTime.ToUniversalTime().Ticks);
        while (!token.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            await server.WaitForConnectionAsync(token);
            AppUpdateLifecycleSession? control = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var launch = await UpdateLifecycleWire.ReadLaunchAsync(server, session, deadline.Token);
                if (Current != null) throw new InvalidOperationException("Another updater already owns the control channel.");
                control = new(launch); Current = control;
                await server.WriteAsync(new byte[] { 1 }, deadline.Token);
                await server.FlushAsync(deadline.Token);
                await control.RunAsync(host, token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (token.IsCancellationRequested) return;
            }
            finally { control?.Dispose(); }
        }
    }
}
