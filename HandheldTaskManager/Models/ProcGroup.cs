using System.ComponentModel;
using System.Windows.Media;
using HandheldTaskManager.Services;

namespace HandheldTaskManager.Models;

/// <summary>One row in the list: an app or a group of same-named processes.</summary>
public sealed class ProcGroup : INotifyPropertyChanged
{
    /// <summary>Windows components that would take the system down with them.</summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm"
    };

    private ImageSource? _icon;
    private string? _iconPath;

    public ProcGroup(string key) => Key = key;

    public string Key { get; }
    public string Name { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string? Path { get; private set; }
    public double Cpu { get; private set; }
    public long Memory { get; private set; }
    public IReadOnlyList<int> Pids { get; private set; } = Array.Empty<int>();
    public IReadOnlyList<IntPtr> Windows { get; private set; } = Array.Empty<IntPtr>();

    public bool HasWindow => Windows.Count > 0;
    public bool IsCritical => CriticalNames.Contains(Name);

    /// <summary>Apps show their window title; background processes show the executable name.</summary>
    public string Primary => HasWindow && Title.Length > 0 ? Title : Name;

    public string Secondary
    {
        get
        {
            string count = Pids.Count == 1 ? $"PID {Pids[0]}" : $"{Pids.Count} processes";
            return HasWindow && Title.Length > 0 ? $"{Name}  ·  {count}" : count;
        }
    }

    public string CpuText => Cpu < 0.05 ? "0%" : Cpu < 10 ? $"{Cpu:0.0}%" : $"{Cpu:0}%";
    public string MemoryText => Format.Bytes(Memory);

    public void Update(GroupData data)
    {
        Name = data.Name;
        Title = data.Title;
        Cpu = data.Cpu;
        Memory = data.Memory;
        Pids = data.Pids;
        Windows = data.Windows;
        if (data.Path != null) Path = data.Path;

        Raise(nameof(Primary));
        Raise(nameof(Secondary));
        Raise(nameof(CpuText));
        Raise(nameof(MemoryText));
        if (Path != null && Path != _iconPath && _icon == null) Raise(nameof(Icon)); // path arrived late
    }

    /// <summary>The executable's icon, loaded the first time the row is shown.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && Path != null && Path != _iconPath)
            {
                _iconPath = Path;
                _ = LoadIconAsync(Path);
            }
            return _icon;
        }
    }

    private async Task LoadIconAsync(string path)
    {
        var image = await ShellThumbnail.GetAsync(path, 64, iconOnly: true);
        if (image != null)
        {
            _icon = image;
            Raise(nameof(Icon));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
