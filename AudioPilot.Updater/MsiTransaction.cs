using System.ComponentModel;

namespace AudioPilot.Updater;

internal sealed class MsiUpdateException(uint code, string operation) : Win32Exception(checked((int)code), $"Windows Installer {operation} failed ({code}).")
{
    internal string Operation { get; } = operation;
    internal string Reason => NativeErrorCode switch { 1618 => "installer-busy", 3010 => "installer-reboot-required", _ => "installer-failed" };
}

internal interface IMsiApi
{
    uint Begin(out uint transaction, out nint ownerChanged);
    uint End(bool commit);
    uint Install(string package, string properties);
    uint EnableLog(string? path);
    uint SetUi(uint level);
    void Close(uint transaction, nint ownerChanged);
}

/// <summary>Reserves Windows Installer before application shutdown and rolls back unless explicitly completed.</summary>
internal sealed class MsiTransaction : IDisposable
{
    internal IMsiApi Api { get; }
    internal bool Ended { get; private set; }
    private readonly uint _transaction;
    private readonly nint _ownerChanged;
    private bool _disposed;

    internal MsiTransaction(IMsiApi api)
    {
        Api = api;
        uint result = api.Begin(out _transaction, out _ownerChanged);
        if (result == 0) return;
        api.Close(_transaction, _ownerChanged);
        throw new MsiUpdateException(result, "begin transaction");
    }

    internal void Complete(bool commit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ended) throw new InvalidOperationException("The installer transaction has already ended.");
        uint result = Api.End(commit);
        if (result != 0) throw new MsiUpdateException(result, commit ? "commit" : "rollback");
        Ended = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { if (!Ended) Complete(false); }
        finally { _disposed = true; Api.Close(_transaction, _ownerChanged); }
    }
}
