using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandheldTaskManager.Controls;
using HandheldTaskManager.Models;
using HandheldTaskManager.Services;

namespace HandheldTaskManager;

public partial class MainWindow : Window
{
    private enum Tab { Apps, Processes, Performance }

    private const int HistoryLength = 60;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GamepadService _pad = new();
    private readonly Sampler _sampler = new();
    private readonly DispatcherTimer _sampleTimer;
    private readonly DispatcherTimer _toastTimer;

    private readonly Dictionary<string, ProcGroup> _groups = new();
    private readonly ObservableCollection<ProcGroup> _view = new();
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _memHistory = new();

    private Tab _tab = Tab.Apps;
    private string _filter = "";
    private bool _sampling;
    private Sample? _last;
    private OnScreenKeyboard? _keyboard;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen;

    private record MenuEntry(string Glyph, string Label, Action Action);

    public MainWindow()
    {
        ThemeManager.Apply(_settings.Theme);
        InitializeComponent();
        ProcList.ItemsSource = _view;

        _sampleTimer = new DispatcherTimer();
        _sampleTimer.Tick += async (_, _) => await SampleAsync();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        _pad.Pressed += OnPad;

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += OnLoaded;
        SizeChanged += (_, _) => DrawCharts();
        Closing += (_, _) =>
        {
            _pad.Stop();
            _sampleTimer.Stop();
            _settings.LastTab = (int)_tab;
            _settings.Save();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = _settings.AlwaysOnTop;
        if (_settings.StartFullscreen) ToggleFullscreen();
        _pad.Start();

        SetTab(Enum.IsDefined(typeof(Tab), _settings.LastTab) ? (Tab)_settings.LastTab : Tab.Apps);
        UpdateSortHeaders();

        await SampleAsync();          // fills the list straight away
        if (_tab != Tab.Performance && !OverlayOpen) FocusList();
        await Task.Delay(600);
        await SampleAsync();          // second pass gives the first real CPU figures
        ApplyRefreshRate();
    }

    // =====================================================================================
    //  Sampling and list maintenance
    // =====================================================================================

    private void ApplyRefreshRate()
    {
        _sampleTimer.Stop();
        if (_settings.RefreshMs <= 0) return; // paused
        _sampleTimer.Interval = TimeSpan.FromMilliseconds(_settings.RefreshMs);
        _sampleTimer.Start();
    }

    private async Task SampleAsync()
    {
        if (_sampling) return; // the previous pass is still running
        _sampling = true;
        try
        {
            var sample = await Task.Run(() => _sampler.Collect());
            ApplySample(sample);
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't read system information: " + ex.Message);
        }
        finally { _sampling = false; }
    }

    private void ApplySample(Sample sample)
    {
        _last = sample;

        var alive = new HashSet<string>();
        foreach (var data in sample.Groups)
        {
            alive.Add(data.Key);
            if (!_groups.TryGetValue(data.Key, out var group))
            {
                group = new ProcGroup(data.Key);
                _groups[data.Key] = group;
            }
            group.Update(data);
        }
        foreach (var key in _groups.Keys.Where(k => !alive.Contains(k)).ToList()) _groups.Remove(key);

        double memPercent = sample.MemTotal > 0 ? 100.0 * (sample.MemTotal - sample.MemAvailable) / sample.MemTotal : 0;
        Push(_cpuHistory, sample.CpuTotal);
        Push(_memHistory, memPercent);

        ChipCpu.Text = $"{sample.CpuTotal:0}%";
        ChipRam.Text = $"{memPercent:0}%";
        ChipBatteryPanel.Visibility = sample.HasBattery && sample.BatteryPercent >= 0 ? Visibility.Visible : Visibility.Collapsed;
        ChipBattery.Text = $"{sample.BatteryPercent}%";
        ChipBatteryIcon.Text = sample.Charging || sample.OnAcPower ? "\uE945" : "\uE83F";

        RebuildView();
        if (_tab == Tab.Performance) UpdatePerformance();
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    /// <summary>Filters and sorts the rows, changing the visible list as little as possible.</summary>
    private void RebuildView()
    {
        if (_tab == Tab.Performance) { UpdateStatus(); return; }

        bool hadFocus = ProcList.IsKeyboardFocusWithin;
        var selected = Selected;

        IEnumerable<ProcGroup> query = _groups.Values;
        if (_tab == Tab.Apps) query = query.Where(g => g.HasWindow);
        if (_filter.Length > 0)
            query = query.Where(g => g.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                                     g.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        var desired = query.ToList();
        desired.Sort(CompareGroups);

        var keep = new HashSet<ProcGroup>(desired);
        for (int i = _view.Count - 1; i >= 0; i--)
            if (!keep.Contains(_view[i])) _view.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < _view.Count && ReferenceEquals(_view[i], desired[i])) continue;
            int existing = _view.IndexOf(desired[i]);
            if (existing >= 0) _view.Move(existing, i);
            else _view.Insert(i, desired[i]);
        }

        EmptyText.Text = _filter.Length > 0 ? "Nothing matches the filter" : "Nothing to show";
        EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Keep the cursor on the same app even when it moves up or down the list.
        if (selected != null && !ReferenceEquals(ProcList.SelectedItem, selected) && keep.Contains(selected))
            ProcList.SelectedItem = selected;
        if (ProcList.SelectedIndex < 0 && _view.Count > 0) ProcList.SelectedIndex = 0;

        // Re-sorting recreates row containers, which drops keyboard focus; put it back on the selected row.
        if (hadFocus && !OverlayOpen && !ProcList.IsKeyboardFocusWithin) FocusList();

        UpdateStatus();
    }

    private int CompareGroups(ProcGroup a, ProcGroup b)
    {
        int result = _settings.SortBy switch
        {
            SortKey.Cpu => a.Cpu.CompareTo(b.Cpu),
            SortKey.Memory => a.Memory.CompareTo(b.Memory),
            _ => string.Compare(a.Primary, b.Primary, StringComparison.CurrentCultureIgnoreCase)
        };
        if (_settings.SortDescending) result = -result;
        return result != 0 ? result : string.CompareOrdinal(a.Key, b.Key); // stable order for ties
    }

    private void SetSort(SortKey key)
    {
        if (_settings.SortBy == key) _settings.SortDescending = !_settings.SortDescending;
        else
        {
            _settings.SortBy = key;
            _settings.SortDescending = key != SortKey.Name; // biggest first for numbers, A-Z for names
        }
        UpdateSortHeaders();
        RebuildView();
    }

    private void CycleSort()
    {
        var next = _settings.SortBy switch
        {
            SortKey.Memory => SortKey.Cpu,
            SortKey.Cpu => SortKey.Name,
            _ => SortKey.Memory
        };
        _settings.SortBy = next;
        _settings.SortDescending = next != SortKey.Name; // biggest first for numbers, A-Z for names
        UpdateSortHeaders();
        RebuildView();
        ShowToast($"Sorted by {next switch { SortKey.Cpu => "CPU", SortKey.Memory => "memory", _ => "name" }}");
    }

    private void UpdateSortHeaders()
    {
        string arrow = _settings.SortDescending ? " ▾" : " ▴";
        HeaderName.Content = "Name" + (_settings.SortBy == SortKey.Name ? arrow : "");
        HeaderCpu.Content = "CPU" + (_settings.SortBy == SortKey.Cpu ? arrow : "");
        HeaderMemory.Content = "Memory" + (_settings.SortBy == SortKey.Memory ? arrow : "");
    }

    // =====================================================================================
    //  Tabs and the performance page
    // =====================================================================================

    private void SetTab(Tab tab)
    {
        _tab = tab;
        var tabs = new (Button Button, Tab Tab)[] { (TabApps, Tab.Apps), (TabProcesses, Tab.Processes), (TabPerformance, Tab.Performance) };
        foreach (var (button, t) in tabs)
        {
            button.SetResourceReference(ForegroundProperty, t == tab ? "AccentBrush" : "TextSecondary");
            button.FontWeight = t == tab ? FontWeights.SemiBold : FontWeights.Normal;
        }

        bool performance = tab == Tab.Performance;
        ListPanel.Visibility = performance ? Visibility.Collapsed : Visibility.Visible;
        PerfPanel.Visibility = performance ? Visibility.Visible : Visibility.Collapsed;

        if (performance)
        {
            UpdateStatus();
            Dispatcher.InvokeAsync(() =>
            {
                UpdatePerformance();
                if (!OverlayOpen) TabPerformance.Focus();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            ProcList.SelectedIndex = -1;
            RebuildView();
            Dispatcher.InvokeAsync(() => { if (!OverlayOpen) SelectIndex(0); }, DispatcherPriority.Loaded);
        }
    }

    private void StepTab(int direction)
    {
        int next = ((int)_tab + direction + 3) % 3;
        SetTab((Tab)next);
    }

    private void UpdatePerformance()
    {
        var s = _last;
        if (s == null) return;

        CpuValue.Text = $"{s.CpuTotal:0}%";
        CpuSub.Text = $"{Environment.ProcessorCount} logical processors";

        double used = s.MemTotal - s.MemAvailable;
        double memPercent = s.MemTotal > 0 ? 100.0 * used / s.MemTotal : 0;
        MemValue.Text = $"{memPercent:0}%";
        MemSub.Text = $"{Format.Bytes(used)} used of {Format.Bytes(s.MemTotal)}";

        if (s.HasBattery && s.BatteryPercent >= 0)
        {
            BatValue.Text = $"{s.BatteryPercent}%";
            BatBar.Value = s.BatteryPercent;
            BatBar.Visibility = Visibility.Visible;
            BatSub.Text = s.Charging ? "Charging"
                : s.OnAcPower ? "Plugged in"
                : s.BatterySecondsLeft > 0 ? $"On battery, about {Format.Duration(TimeSpan.FromSeconds(s.BatterySecondsLeft))} left"
                : "On battery";
        }
        else
        {
            BatValue.Text = "None";
            BatSub.Text = "No battery detected";
            BatBar.Visibility = Visibility.Collapsed;
        }

        NetDown.Text = Format.Bytes(s.NetDownBytesPerSec) + "/s";
        NetUp.Text = Format.Bytes(s.NetUpBytesPerSec) + "/s";
        DriveItems.ItemsSource = s.Drives;

        SysInfo.Text = $"Up time: {Format.Duration(s.Uptime)}\n" +
                       $"Processes: {s.ProcessCount}\n" +
                       $"Device: {Environment.MachineName}\n" +
                       $"Running as administrator: {(IsElevated ? "yes" : "no")}";
        DrawCharts();
    }

    private void DrawCharts()
    {
        if (_tab != Tab.Performance) return;
        Plot(CpuLine, CpuChart, _cpuHistory);
        Plot(MemLine, MemChart, _memHistory);
    }

    /// <summary>Draws a 0-100 history as a line that scrolls in from the right.</summary>
    private static void Plot(System.Windows.Shapes.Polyline line, FrameworkElement host, List<double> values)
    {
        double width = host.ActualWidth, height = host.ActualHeight;
        if (width <= 0 || height <= 0 || values.Count == 0) return;

        var points = new PointCollection(values.Count);
        int offset = HistoryLength - values.Count;
        for (int i = 0; i < values.Count; i++)
        {
            double x = width * (offset + i) / (HistoryLength - 1);
            double y = height - 1 - Math.Clamp(values[i], 0, 100) / 100 * (height - 2);
            points.Add(new Point(x, y));
        }
        line.Points = points;
    }

    // =====================================================================================
    //  Input: gamepad, keyboard, touch / mouse
    // =====================================================================================

    private void OnPad(PadButton button)
    {
        if (!IsActive) return; // never react while a game or another app has focus

        if (_keyboard != null)
        {
            switch (button)
            {
                case PadButton.X: _keyboard.Backspace(); return;
                case PadButton.Y: _keyboard.Append(" "); return;
                case PadButton.LS: _keyboard.ToggleShift(); return;
                case PadButton.Menu: _keyboard.Confirm(); return;
            }
        }

        switch (button)
        {
            case PadButton.Up: Nav(FocusNavigationDirection.Up); return;
            case PadButton.Down: Nav(FocusNavigationDirection.Down); return;
            case PadButton.Left: Nav(FocusNavigationDirection.Left); return;
            case PadButton.Right: Nav(FocusNavigationDirection.Right); return;
            case PadButton.A: Activate(); return;
            case PadButton.B: Back(); return;
        }

        if (OverlayOpen) return;

        switch (button)
        {
            case PadButton.X: ConfirmEndTask(Selected); break;
            case PadButton.Y: if (_tab != Tab.Performance) CycleSort(); break;
            case PadButton.LB: StepTab(-1); break;
            case PadButton.RB: StepTab(1); break;
            case PadButton.LT: MovePage(-1); break;
            case PadButton.RT: MovePage(1); break;
            case PadButton.Menu: ShowMainMenu(); break;
            case PadButton.View: _ = SampleAsync(); ShowToast("Refreshed"); break;
            case PadButton.RS: PromptFilter(); break;
        }
    }

    /// <summary>D-pad / stick / arrow-key movement.</summary>
    private void Nav(FocusNavigationDirection direction)
    {
        var focused = Keyboard.FocusedElement as UIElement;

        if (OverlayOpen)
        {
            if (focused != null && focused.IsVisible && OverlayHost.IsKeyboardFocusWithin)
                focused.MoveFocus(new TraversalRequest(direction));
            else
                OverlayCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }

        bool vertical = direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down;

        if (_tab == Tab.Performance)
        {
            if (vertical)
            {
                double step = direction == FocusNavigationDirection.Down ? 90 : -90;
                PerfPanel.ScrollToVerticalOffset(PerfPanel.VerticalOffset + step);
                return;
            }
            if (focused == null || !focused.IsVisible || !TopBar.IsKeyboardFocusWithin) { TabPerformance.Focus(); return; }
            focused.MoveFocus(new TraversalRequest(direction));
            return;
        }

        if (ProcList.IsKeyboardFocusWithin)
        {
            if (!vertical) return;
            int index = Math.Max(0, ProcList.SelectedIndex);
            if (direction == FocusNavigationDirection.Up && index == 0) { CurrentTabButton.Focus(); return; }
            SelectIndex(index + (direction == FocusNavigationDirection.Down ? 1 : -1));
            return;
        }

        if (focused == null || !focused.IsVisible) { FocusList(); return; }
        if (direction == FocusNavigationDirection.Down) { FocusList(); return; }
        focused.MoveFocus(new TraversalRequest(direction));
    }

    private Button CurrentTabButton => _tab switch
    {
        Tab.Apps => TabApps,
        Tab.Processes => TabProcesses,
        _ => TabPerformance
    };

    /// <summary>The A button: press the focused button or open the actions for the selected row.</summary>
    private void Activate()
    {
        if (Keyboard.FocusedElement is Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }
        if (OverlayOpen || _tab == Tab.Performance) return;
        if (ProcList.IsKeyboardFocusWithin) ShowActionMenu(Selected);
        else FocusList();
    }

    /// <summary>The B button / Escape: cancel, close, clear the filter, return to the list.</summary>
    private void Back()
    {
        if (OverlayOpen)
        {
            if (_keyboard != null) _keyboard.Cancel();
            else CloseOverlay();
            return;
        }
        if (_filter.Length > 0) { _filter = ""; RebuildView(); return; }
        if (_tab != Tab.Performance && !ProcList.IsKeyboardFocusWithin) FocusList();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        if (OverlayOpen)
        {
            // Menus and the on-screen keyboard use WPF's own arrow / Enter handling.
            if (key == Key.Escape) { Back(); e.Handled = true; }
            return;
        }

        bool inList = ProcList.IsKeyboardFocusWithin;
        bool handled = true;

        if (mods == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Up: Nav(FocusNavigationDirection.Up); break;
                case Key.Down: Nav(FocusNavigationDirection.Down); break;
                case Key.Left: Nav(FocusNavigationDirection.Left); break;
                case Key.Right: Nav(FocusNavigationDirection.Right); break;
                case Key.Escape: Back(); break;
                case Key.F5: _ = SampleAsync(); break;
                case Key.F10: ShowMainMenu(); break;
                case Key.F11: ToggleFullscreen(); break;
                case Key.Delete when inList: ConfirmEndTask(Selected); break;
                case Key.Enter when inList: ShowActionMenu(Selected); break;
                case Key.Apps when inList: ShowActionMenu(Selected); break;
                case Key.PageUp when inList: MovePage(-1); break;
                case Key.PageDown when inList: MovePage(1); break;
                case Key.Home when inList: SelectIndex(0); break;
                case Key.End when inList: SelectIndex(_view.Count - 1); break;
                default: handled = false; break;
            }
        }
        else if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.F: PromptFilter(); break;
                case Key.Tab: StepTab(1); break;
                case Key.N: PromptRun(); break;
                default: handled = false; break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab)
        {
            StepTab(-1);
        }
        else
        {
            handled = false;
        }

        e.Handled = handled;
    }

    // ---- moving the cursor inside the list --------------------------------------------------

    private ProcGroup? Selected => ProcList.SelectedItem as ProcGroup;

    private void FocusList() => SelectIndex(Math.Max(0, ProcList.SelectedIndex));

    private void SelectIndex(int index)
    {
        if (_tab == Tab.Performance) return;
        if (_view.Count == 0) { CurrentTabButton.Focus(); return; }
        index = Math.Clamp(index, 0, _view.Count - 1);
        ProcList.SelectedIndex = index;
        ProcList.ScrollIntoView(_view[index]);
        ProcList.UpdateLayout();
        if (ProcList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container) container.Focus();
        else ProcList.Focus();
    }

    private void MovePage(int direction)
    {
        if (_tab == Tab.Performance)
        {
            PerfPanel.ScrollToVerticalOffset(PerfPanel.VerticalOffset + direction * PerfPanel.ViewportHeight * 0.8);
            return;
        }
        if (_view.Count == 0) return;
        int rows = Math.Max(1, (int)(ProcList.ActualHeight / 62) - 1);
        SelectIndex(Math.Max(0, ProcList.SelectedIndex) + direction * rows);
    }

    // ---- touch / mouse ------------------------------------------------------------------------

    /// <summary>Tap a row to see what you can do with it.</summary>
    private void Row_LeftUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ProcGroup group })
            Dispatcher.InvokeAsync(() => { if (!OverlayOpen) ShowActionMenu(group); });
    }

    private void Row_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ProcGroup group }) return;
        ProcList.SelectedItem = group;
        e.Handled = true;
        ShowActionMenu(group);
    }

    private void Overlay_BackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, OverlayHost)) Back();
    }

    /// <summary>Stops WPF from nudging the whole window when a touch scroll hits the end of a list.</summary>
    private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e) => e.Handled = true;

    private void TabApps_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Apps);
    private void TabProcesses_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Processes);
    private void TabPerformance_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Performance);
    private void HeaderName_Click(object sender, RoutedEventArgs e) => SetSort(SortKey.Name);
    private void HeaderCpu_Click(object sender, RoutedEventArgs e) => SetSort(SortKey.Cpu);
    private void HeaderMemory_Click(object sender, RoutedEventArgs e) => SetSort(SortKey.Memory);
    private void Search_Click(object sender, RoutedEventArgs e) => PromptFilter();
    private void Menu_Click(object sender, RoutedEventArgs e) => ShowMainMenu();

    // =====================================================================================
    //  Process actions
    // =====================================================================================

    private static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    private void SwitchTo(ProcGroup group)
    {
        if (!group.HasWindow) return;
        IntPtr hwnd = group.Windows[0];
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (!Native.SetForegroundWindow(hwnd)) ShowToast("Windows wouldn't switch to that app");
    }

    /// <summary>Asks the app to close, the same as pressing its X button, so it can save first.</summary>
    private void CloseWindows(ProcGroup group)
    {
        foreach (var hwnd in group.Windows)
            Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        ShowToast($"Asked {group.Primary} to close");
        _ = RefreshSoonAsync();
    }

    private void ConfirmEndTask(ProcGroup? group)
    {
        if (group == null || _tab == Tab.Performance) return;
        if (group.IsCritical)
        {
            ShowToast($"{group.Name} is a Windows system process. Ending it would crash or sign out the device.");
            return;
        }

        string what = group.Pids.Count == 1 ? group.Primary : $"{group.Primary} ({group.Pids.Count} processes)";
        ShowMenu($"End {what}?", new[]
        {
            new MenuEntry("\uE711", "Cancel", () => { }),
            new MenuEntry("\uE74D", "End task", () => EndTask(group)),
        }, "It closes immediately. Anything unsaved in it is lost.");
    }

    private void EndTask(ProcGroup group)
    {
        int failed = 0;
        bool denied = false;
        int self = Environment.ProcessId;

        foreach (int pid in group.Pids.ToList())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                // Ending this app from its own list should only close this app, not what launched from it.
                process.Kill(entireProcessTree: pid != self);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* exited while we were asking */ }
            catch (Win32Exception ex)
            {
                failed++;
                if (ex.NativeErrorCode == 5) denied = true; // ERROR_ACCESS_DENIED
            }
            catch (Exception) { failed++; }
        }

        if (failed == 0) ShowToast($"Ended {group.Name}");
        else if (denied && !IsElevated) ShowToast($"Windows blocked ending {group.Name}. Use Menu > Restart as administrator and try again.");
        else ShowToast($"Couldn't end {failed} of {group.Pids.Count} processes in {group.Name}");
        _ = RefreshSoonAsync();
    }

    private void SetPriority(ProcGroup group, ProcessPriorityClass priority)
    {
        int failed = 0;
        foreach (int pid in group.Pids.ToList())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.PriorityClass = priority;
            }
            catch (ArgumentException) { /* already gone */ }
            catch { failed++; }
        }
        ShowToast(failed == 0 ? $"{group.Name} priority set to {PriorityName(priority)}"
            : $"Couldn't change priority for {failed} of {group.Pids.Count} processes" + (IsElevated ? "" : " (try running as administrator)"));
    }

    private static string PriorityName(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => "Low",
        ProcessPriorityClass.BelowNormal => "Below normal",
        ProcessPriorityClass.AboveNormal => "Above normal",
        ProcessPriorityClass.High => "High",
        ProcessPriorityClass.RealTime => "Realtime",
        _ => "Normal"
    };

    private async Task RefreshSoonAsync()
    {
        await Task.Delay(500);
        await SampleAsync();
    }

    private void RunProcess(string file, string arguments = "")
    {
        try { Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    private void RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            _settings.LastTab = (int)_tab;
            _settings.Save();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception) { ShowToast("Administrator access was declined"); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    // =====================================================================================
    //  Menus
    // =====================================================================================

    private void ShowActionMenu(ProcGroup? group)
    {
        if (group == null) return;
        var entries = new List<MenuEntry>();

        if (group.HasWindow)
        {
            entries.Add(new MenuEntry("\uE8A7", "Switch to", () => SwitchTo(group)));
            entries.Add(new MenuEntry("\uE8BB", "Close (lets the app save first)", () => CloseWindows(group)));
        }
        entries.Add(new MenuEntry("\uE74D", "End task (force)", () => ConfirmEndTask(group)));
        entries.Add(new MenuEntry("\uE9D9", "Set priority", () => ShowPriorityMenu(group)));
        if (group.Path != null)
            entries.Add(new MenuEntry("\uE8DA", "Open file location", () => RunProcess("explorer.exe", $"/select,\"{group.Path}\"")));
        entries.Add(new MenuEntry("\uE946", "Details", () => ShowDetails(group)));

        ShowMenu(group.Primary, entries, $"{group.Name}  ·  CPU {group.CpuText}  ·  {group.MemoryText}");
    }

    private void ShowPriorityMenu(ProcGroup group)
    {
        ProcessPriorityClass? current = null;
        try
        {
            using var process = Process.GetProcessById(group.Pids[0]);
            current = process.PriorityClass;
        }
        catch { /* not readable without administrator rights */ }

        MenuEntry Option(ProcessPriorityClass priority) =>
            new(current == priority ? "\uE73E" : "", PriorityName(priority), () => SetPriority(group, priority));

        ShowMenu("Priority for " + group.Name, new[]
        {
            Option(ProcessPriorityClass.High),
            Option(ProcessPriorityClass.AboveNormal),
            Option(ProcessPriorityClass.Normal),
            Option(ProcessPriorityClass.BelowNormal),
            Option(ProcessPriorityClass.Idle),
        }, "Higher priority gives a game more CPU time; lower keeps background apps out of the way.");
    }

    private void ShowDetails(ProcGroup group)
    {
        TextBlock Value(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        var rows = new List<(string, TextBlock)> { ("Name", Value(group.Name)) };
        if (group.Title.Length > 0) rows.Add(("Window", Value(group.Title)));
        rows.Add(("Processes", Value(group.Pids.Count.ToString())));
        rows.Add(("Process IDs", Value(string.Join(", ", group.Pids.Take(24)) + (group.Pids.Count > 24 ? ", …" : ""))));
        rows.Add(("CPU", Value(group.CpuText)));
        rows.Add(("Memory", Value(group.MemoryText)));
        rows.Add(("Location", Value(group.Path ?? "Not available (system process)")));

        try
        {
            using var process = Process.GetProcessById(group.Pids[0]);
            rows.Add(("Priority", Value(PriorityName(process.PriorityClass))));
            rows.Add(("Started", Value(process.StartTime.ToString("g"))));
            rows.Add(("Threads", Value(process.Threads.Count.ToString())));
        }
        catch { /* protected or already gone */ }

        ShowInfo(group.Primary, rows);
    }

    private void ShowMainMenu()
    {
        string speed = _settings.RefreshMs switch
        {
            <= 0 => "paused",
            <= 1000 => "fast (1 s)",
            <= 2000 => "normal (2 s)",
            _ => "slow (5 s)"
        };

        var entries = new List<MenuEntry>
        {
            new("\uE721", _filter.Length > 0 ? $"Change filter (“{_filter}”)" : "Filter by name", PromptFilter),
            new("\uE8CB", "Sort by", ShowSortMenu),
            new("\uE72C", "Refresh now", () => _ = SampleAsync()),
            new("\uE916", $"Update speed: {speed}", ShowSpeedMenu),
            new("\uE768", "Run new task", PromptRun),
            new("\uE718", _settings.AlwaysOnTop ? "Always on top: on" : "Always on top: off", () =>
            {
                _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
                Topmost = _settings.AlwaysOnTop;
                _settings.Save();
            }),
            new("\uE740", _fullscreen ? "Exit full screen" : "Full screen", ToggleFullscreen),
        };
        if (!IsElevated) entries.Add(new MenuEntry("\uE7EF", "Restart as administrator", RestartAsAdministrator));
        entries.Add(new MenuEntry("\uE713", "Settings", ShowSettings));
        entries.Add(new MenuEntry("\uE7E8", "Close Task Manager", Close));
        ShowMenu("Menu", entries);
    }

    private void ShowSortMenu()
    {
        MenuEntry By(SortKey key, string label) => new(_settings.SortBy == key ? "\uE73E" : "", label, () => SetSort(key));
        ShowMenu("Sort by", new[]
        {
            By(SortKey.Memory, "Memory"),
            By(SortKey.Cpu, "CPU"),
            By(SortKey.Name, "Name"),
        }, "Choose the current one again to reverse the order.");
    }

    private void ShowSpeedMenu()
    {
        MenuEntry Speed(int ms, string label) => new(_settings.RefreshMs == ms ? "\uE73E" : "", label, () =>
        {
            _settings.RefreshMs = ms;
            _settings.Save();
            ApplyRefreshRate();
        });
        ShowMenu("Update speed", new[]
        {
            Speed(1000, "Fast (every second)"),
            Speed(2000, "Normal (every 2 seconds)"),
            Speed(5000, "Slow (every 5 seconds, lightest on battery)"),
            Speed(0, "Paused"),
        });
    }

    private void ShowSettings()
    {
        string theme = _settings.Theme switch
        {
            AppTheme.Dark => "Dark",
            AppTheme.Light => "Light",
            _ => "Follow Windows"
        };

        ShowMenu("Settings", new[]
        {
            new MenuEntry("\uE790", $"Theme: {theme}", () =>
            {
                _settings.Theme = (AppTheme)(((int)_settings.Theme + 1) % 3);
                ThemeManager.Apply(_settings.Theme);
                _settings.Save();
                ShowSettings();
            }),
            new MenuEntry("\uE740", $"Start in full screen: {(_settings.StartFullscreen ? "on" : "off")}", () =>
            {
                _settings.StartFullscreen = !_settings.StartFullscreen;
                _settings.Save();
                ShowSettings();
            }),
            new MenuEntry("\uE7FC", "Controls", ShowControlsHelp),
            new MenuEntry("\uE711", "Close", () => { }),
        });
    }

    private void ShowControlsHelp()
    {
        var rows = new (string, string)[]
        {
            ("D-pad / left stick", "Move"),
            ("A", "Actions for the selected app or process"),
            ("B", "Back, cancel, clear the filter"),
            ("X", "End task (asks first)"),
            ("Y", "Change sort order"),
            ("LB / RB", "Previous / next tab"),
            ("LT / RT", "Page up / page down"),
            ("View", "Refresh now"),
            ("Menu", "Main menu"),
            ("Right stick click", "Filter by name"),
            ("Touch", "Tap a row for actions, tap a column header to sort"),
            ("Keyboard", "Arrows, Enter, Del, Ctrl+F, Ctrl+N, Ctrl+Tab, F5, F10, F11"),
        };
        ShowInfo("Controls", rows.Select(r => (r.Item1, new TextBlock { Text = r.Item2, TextWrapping = TextWrapping.Wrap })));
    }

    private void PromptFilter() => Prompt("Filter by name", _filter, text =>
    {
        _filter = text;
        if (_tab == Tab.Performance) SetTab(Tab.Processes);
        else
        {
            ProcList.SelectedIndex = -1;
            RebuildView();
        }
    });

    private void PromptRun() => Prompt("Run new task (program, folder or web address)", "", text =>
    {
        if (text.Length == 0) return;
        RunProcess(text);
        _ = RefreshSoonAsync();
    });

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.Normal; // required so the borderless window covers the taskbar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
        }
    }

    // =====================================================================================
    //  Overlay plumbing (menus, dialogs, on-screen keyboard), toast, status
    // =====================================================================================

    private bool OverlayOpen => OverlayHost.Visibility == Visibility.Visible;

    private void ShowOverlay(UIElement content, UIElement? focus)
    {
        OverlayContent.Content = content;
        OverlayHost.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(OverlayContent.Content, content)) return; // already replaced or closed
            if (focus != null) focus.Focus();
            else OverlayCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Loaded);
    }

    private void CloseOverlay()
    {
        if (!OverlayOpen) return;
        OverlayHost.Visibility = Visibility.Collapsed;
        OverlayContent.Content = null;
        _keyboard = null;
        Dispatcher.InvokeAsync(() =>
        {
            if (OverlayOpen) return;
            if (_tab == Tab.Performance) TabPerformance.Focus();
            else FocusList();
        }, DispatcherPriority.Loaded);
    }

    private double OverlayMaxContentHeight => Math.Max(160, ActualHeight - 200);

    private StackPanel Titled(string title, string? subtitle)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, subtitle == null ? 12 : 2)
        });
        if (subtitle != null)
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 12) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(sub);
        }
        return panel;
    }

    private void ShowMenu(string title, IEnumerable<MenuEntry> entries, string? subtitle = null)
    {
        var iconFont = (FontFamily)FindResource("IconFont");
        var list = new StackPanel();
        Button? first = null;

        foreach (var entry in entries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = entry.Glyph, FontFamily = iconFont, FontSize = 18, Width = 36, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = entry.Label, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button { Content = row, Style = (Style)FindResource("MenuButton") };
            button.Click += (_, _) =>
            {
                CloseOverlay();
                entry.Action();
            };
            list.Children.Add(button);
            first ??= button;
        }

        var panel = Titled(title, subtitle);
        panel.Width = Math.Min(440, Math.Max(280, ActualWidth - 80));
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            Focusable = false,
            MaxHeight = OverlayMaxContentHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        });
        ShowOverlay(panel, first);
    }

    /// <summary>A read-only label/value sheet with a Close button.</summary>
    private void ShowInfo(string title, IEnumerable<(string Label, TextBlock Value)> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int index = 0;
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Margin = new Thickness(6, 5, 18, 5) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            value.Margin = new Thickness(0, 5, 6, 5);
            Grid.SetRow(name, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
            index++;
        }

        var close = new Button { Content = "Close", Style = (Style)FindResource("MenuButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => CloseOverlay();

        var panel = Titled(title, null);
        panel.Width = Math.Min(540, Math.Max(280, ActualWidth - 80));
        panel.Children.Add(new ScrollViewer
        {
            Content = grid,
            Focusable = false,
            MaxHeight = OverlayMaxContentHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        });
        panel.Children.Add(close);
        ShowOverlay(panel, close);
    }

    /// <summary>Asks for text with the built-in on-screen keyboard.</summary>
    private void Prompt(string title, string initial, Action<string> onDone)
    {
        var keyboard = new OnScreenKeyboard(title, initial) { Width = Math.Min(720, Math.Max(320, ActualWidth - 80)) };
        keyboard.Confirmed += text =>
        {
            CloseOverlay();
            onDone(text);
        };
        keyboard.Cancelled += CloseOverlay;
        ShowOverlay(keyboard, null);
        _keyboard = keyboard;
        Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_keyboard, keyboard)) keyboard.FocusFirstKey(); }, DispatcherPriority.Loaded);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_tab == Tab.Performance) parts.Add("Live system overview");
        else parts.Add(_tab == Tab.Apps ? $"{_view.Count} open apps" : $"{_view.Count} process groups");
        if (_last != null) parts.Add($"{_last.ProcessCount} processes running");
        if (_filter.Length > 0 && _tab != Tab.Performance) parts.Add($"filter “{_filter}”");
        if (_settings.RefreshMs <= 0) parts.Add("updates paused");
        StatusText.Text = string.Join("   ·   ", parts);
    }
}
