using System.Collections.Concurrent;

namespace JunimoGate.Mods;

internal sealed class RepositoryChangeSignal
{
    private event Action? changed;

    public event Action? Changed
    {
        add => changed += value;
        remove => changed -= value;
    }

    public void Publish() => changed?.Invoke();
}

internal static class ModRepositoryChangeSignals
{
    public static readonly ConcurrentDictionary<string, RepositoryChangeSignal> Libraries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, RepositoryChangeSignal> LibraryBundles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, RepositoryChangeSignal> Profiles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, RepositoryChangeSignal> ActiveProfiles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
