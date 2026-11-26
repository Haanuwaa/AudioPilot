namespace AudioPilot.UpdateProtocol;

public static class SettingsStorageLimits
{
    public const int MaximumSettingsFileBytes = 4 * 1024 * 1024;
    public const int BackupRetentionCount = 5;

    // Include the current file, a pending backup, and space for placement and fallback markers.
    public const int SnapshotBudgetBytes = (BackupRetentionCount + 3) * MaximumSettingsFileBytes;
}
