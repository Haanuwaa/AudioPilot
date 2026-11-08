using AudioPilot.Helpers;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private ListManagerWindow? _listManager;

    private void ShowListManager(object? parameter) =>
        ShowListManager(parameter switch
        {
            "Input" => ListManagerMode.Input,
            "Routines" => ListManagerMode.Routines,
            _ => ListManagerMode.Output,
        });

    internal void ShowListManager(ListManagerMode mode, string? deviceId = null)
    {
        if (IsRuntimeShutdownRequested || _isCleaningUp) return;
        if (_listManager != null)
        {
            _ = _listManager.Activate();
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken);
        var window = new ListManagerWindow(this, _audio, mode, lifetime.Token, deviceId);
        _listManager = window;
        try
        {
            DialogWindowHelper.ShowAppOwnedDialog(window);
        }
        finally
        {
            window.Detach();
            _listManager = null;
        }
    }

    internal bool CanEditManagedList => !IsRuntimeShutdownRequested && !_isCleaningUp && !IsResettingSettings && !IsSavingRoutines;

    internal Task<ListManagerDeviceInventory> ReadListManagerDevicesAsync(bool output, CancellationToken token)
    {
        return ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            AudioDeviceInventory active = _audio.ReadActiveDeviceInventory(output);
            Dictionary<Role, string> defaults = [];
            List<Role> unreadableRoles = [];
            using var enumerator = new MMDeviceEnumerator();
            foreach (Role role in Enum.GetValues<Role>())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using MMDevice? endpoint = enumerator.GetDefaultAudioEndpoint(output ? DataFlow.Render : DataFlow.Capture, role);
                    if (endpoint != null) defaults[role] = endpoint.ID;
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070490))
                {
                    // Windows has no default for this role.
                }
                catch (Exception ex)
                {
                    unreadableRoles.Add(role);
                    _logger.Debug("ListManager", () => $"default-role-read-failed | role={role} hresult=0x{ex.HResult:X8}", nameof(ReadListManagerDevicesAsync));
                }
            }
            return new ListManagerDeviceInventory(active, defaults, unreadableRoles, IsComplete: active.IsComplete);
        }, token);
    }

    internal void ReportListManagerDiscoveryFailure(Exception exception)
    {
        _logger.Warning("ListManager", "device-inventory-read-failed", nameof(ReadListManagerDevicesAsync), exception);
    }
}
