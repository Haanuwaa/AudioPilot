using AudioPilot.Platform;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class InMemoryUserRegistryAccessor : IUserRegistryAccessor
{
    private readonly Dictionary<(string Path, string Name), object> _values = [];

    public bool ThrowOnAccess { get; set; }
    public bool ThrowOnDeleteSubKeyTree { get; set; }

    public object? GetValue(string subKeyPath, string valueName)
    {
        ThrowIfAccessDenied();
        return _values.GetValueOrDefault(Normalize(subKeyPath, valueName));
    }

    public void SetValue(string subKeyPath, string valueName, object value)
    {
        ThrowIfAccessDenied();
        _values[Normalize(subKeyPath, valueName)] = value;
    }

    public void DeleteValue(string subKeyPath, string valueName)
    {
        ThrowIfAccessDenied();
        _values.Remove(Normalize(subKeyPath, valueName));
    }

    public bool HasValuesOrSubKeys(string subKeyPath)
    {
        ThrowIfAccessDenied();
        string normalizedPath = NormalizePath(subKeyPath);
        return _values.Keys.Any(key =>
            key.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
            || key.Path.StartsWith(normalizedPath + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteSubKeyTree(string subKeyPath)
    {
        ThrowIfAccessDenied();
        if (ThrowOnDeleteSubKeyTree)
        {
            throw new UnauthorizedAccessException("Simulated HKCU delete denial.");
        }

        string normalizedPath = NormalizePath(subKeyPath);
        foreach ((string Path, string Name) key in _values.Keys
                     .Where(key => key.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
                         || key.Path.StartsWith(normalizedPath + "\\", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _values.Remove(key);
        }
    }

    private static (string Path, string Name) Normalize(string path, string name)
    {
        return (NormalizePath(path), name.ToUpperInvariant());
    }

    private static string NormalizePath(string path)
    {
        return path.TrimEnd('\\').ToUpperInvariant();
    }

    private void ThrowIfAccessDenied()
    {
        if (ThrowOnAccess)
        {
            throw new UnauthorizedAccessException("Simulated HKCU access denial.");
        }
    }
}
