using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Il2CppDumper;
using Il2CppDumperEx.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Il2CppDumperEx.UI;

public record VersionItem(int? Version, string DisplayName);

public record LogEntry(string RawText, Brush ForegroundBrush);

public partial class MainWindow : FluentWindow
{
    private static readonly SolidColorBrush BrushInfo = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8)));
    private static readonly SolidColorBrush BrushNotice = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
    private static readonly SolidColorBrush BrushWarn = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
    private static readonly SolidColorBrush BrushError = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
    private static readonly SolidColorBrush BrushSuccess = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly SolidColorBrush BrushSubtle = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)));

    private static readonly SolidColorBrush StateDotGreen = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly SolidColorBrush StateDotBlue = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
    private static readonly SolidColorBrush StateDotRed = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly SolidColorBrush StateDotAmber = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));

    private static readonly SolidColorBrush BrushPrgBlue = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
    private static readonly SolidColorBrush BrushPrgGreen = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly SolidColorBrush BrushPrgRed = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly SolidColorBrush BrushPrgAmber = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));

    private static readonly SolidColorBrush CardBgDefault = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)));
    private static readonly SolidColorBrush CardBorderDefault = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x36)));
    private static readonly SolidColorBrush CardBgHover = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x28, 0x38, 0xBD, 0xF8)));
    private static readonly SolidColorBrush CardBorderHover = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));

    private static SolidColorBrush FreezeBrush(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private readonly ObservableCollection<LogEntry> _logEntries = [];
    private ScrollViewer? _logScrollViewer;
    private CancellationTokenSource? _cts;
    private string? _lastOutputDir;
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new();
    private readonly Stopwatch _stopwatch = new();

    private string? _detectedUnityVersion;
    private string? _detectedMetadataVersion;

    public MainWindow()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        PopulateMetadataVersions();

        LstLog.ItemsSource = _logEntries;
        PrgBar.Foreground = BrushPrgBlue;

        _toastTimer.Interval = TimeSpan.FromSeconds(2);
        _toastTimer.Tick += (s, e) =>
        {
            LblToast.Visibility = Visibility.Collapsed;
            _toastTimer.Stop();
        };

        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(100);
        _elapsedTimer.Tick += (s, e) =>
        {
            StatusTimer.Text = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        };

        SetStateReady();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTBORDER = 18;
        const int HTLEFT = 10;
        const int HTBOTTOMRIGHT = 17;

        if (msg == WM_NCHITTEST)
        {
            var hit = DefWindowProc(hwnd, msg, wParam, lParam);
            int hitVal = hit.ToInt32();
            // If the hit-test is on any border or corner resize handles (HTLEFT through HTBOTTOMRIGHT),
            // suppress the resize cursor by returning HTBORDER (static border)
            if (hitVal >= HTLEFT && hitVal <= HTBOTTOMRIGHT)
            {
                handled = true;
                return new IntPtr(HTBORDER);
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer = FindVisualChild<ScrollViewer>(LstLog);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private void PopulateMetadataVersions()
    {
        var items = new List<VersionItem>
        {
            new(null, "Auto-detect (recommended)")
        };

        foreach (var (version, label) in MetadataValidator.SupportedVersionEntries)
        {
            items.Add(new(version, label));
        }

        CmbForceVersion.ItemsSource = items;
        CmbForceVersion.DisplayMemberPath = nameof(VersionItem.DisplayName);
        CmbForceVersion.SelectedIndex = 0;
    }

    #region State & Status Bar Helpers

    private void SetStateReady()
    {
        StatusDot.Fill = StateDotGreen;
        StatusText.Text = "Ready";
        PrgRing.Visibility = Visibility.Collapsed;
    }

    private void SetStateRunning()
    {
        StatusDot.Fill = StateDotBlue;
        StatusText.Text = "Running";
        PrgRing.Visibility = Visibility.Visible;
    }

    private void SetStateDone()
    {
        StatusDot.Fill = StateDotGreen;
        StatusText.Text = "Completed";
        PrgRing.Visibility = Visibility.Collapsed;
    }

    private void SetStateError()
    {
        StatusDot.Fill = StateDotRed;
        StatusText.Text = "Error";
        PrgRing.Visibility = Visibility.Collapsed;
    }

    private void SetStateCancelled()
    {
        StatusDot.Fill = StateDotAmber;
        StatusText.Text = "Cancelled";
        PrgRing.Visibility = Visibility.Collapsed;
    }

    private void UpdateVersionsDisplay()
    {
        string unity = string.IsNullOrEmpty(_detectedUnityVersion) ? "Auto-detect" : _detectedUnityVersion;
        string meta = string.IsNullOrEmpty(_detectedMetadataVersion) ? "Auto-detect" : _detectedMetadataVersion;
        StatusVersions.Text = $"Unity: {unity}  •  Metadata: {meta}";
    }

    private void SetProgressAnimated(double targetValue)
    {
        var animation = new DoubleAnimation
        {
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PrgBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
    }

    #endregion

    #region Drag and Drop Handling

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            TargetCard.BorderBrush = CardBorderHover;
            TargetCard.Background = CardBgHover;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        ResetTargetCardStyle();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        ResetTargetCardStyle();

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                SetGamePath(files[0]);
            }
        }
    }

    private void ResetTargetCardStyle()
    {
        TargetCard.BorderBrush = CardBorderDefault;
        TargetCard.Background = CardBgDefault;
    }

    private void SetGamePath(string path)
    {
        TxtGamePath.Text = path;
        try
        {
            string gameName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (gameName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                gameName = Path.GetFileNameWithoutExtension(gameName);
            }
            if (!string.IsNullOrEmpty(gameName))
            {
                TxtOutputDir.Text = Path.Combine(Directory.GetCurrentDirectory(), "output", gameName);
            }
        }
        catch
        {
        }
    }

    #endregion

    #region Browse Handlers

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Game Root Folder or Data Directory"
        };

        if (dlg.ShowDialog(this) == true)
        {
            SetGamePath(dlg.FolderName);
        }
    }

    private void BtnBrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Unity Game Executable",
            Filter = "Game Executable (*.exe)|*.exe|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            SetGamePath(dlg.FileName);
        }
    }

    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Output Directory"
        };

        if (dlg.ShowDialog(this) == true)
        {
            TxtOutputDir.Text = dlg.FolderName;
        }
    }

    #endregion

    #region Dump Pipeline Execution

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        string gamePath = TxtGamePath.Text.Trim();
        if (string.IsNullOrEmpty(gamePath))
        {
            System.Windows.MessageBox.Show(this, "Please specify a game folder, <Game>_Data directory, or executable path.", "Target Required", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtGamePath.Focus();
            return;
        }

        string? outputDir = string.IsNullOrWhiteSpace(TxtOutputDir.Text) ? null : TxtOutputDir.Text.Trim();

        var selectedVersion = CmbForceVersion.SelectedItem as VersionItem;
        int? forceVersion = selectedVersion?.Version;

        var dumperConfig = new Config
        {
            GenerateDummyDll = ChkDummyDll.IsChecked == true,
            GenerateStruct = ChkStruct.IsChecked == true,
            DumpMethod = ChkDumpMethod.IsChecked == true,
            DumpMethodOffset = ChkDumpMethodOffset.IsChecked == true,
            DumpField = ChkDumpField.IsChecked == true,
            DumpFieldOffset = ChkDumpFieldOffset.IsChecked == true,
            DumpProperty = ChkDumpProperty.IsChecked == true,
            DumpAttribute = ChkDumpAttribute.IsChecked == true,
            DumpTypeDefIndex = true,
            DummyDllAddToken = true,
            RequireAnyKey = false,
            ForceDump = false,
            NoRedirectedPointer = false
        };

        BtnStart.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        BtnOpenOutput.Visibility = Visibility.Collapsed;
        SetStateRunning();

        _detectedUnityVersion = null;
        _detectedMetadataVersion = null;
        UpdateVersionsDisplay();

        PrgBar.Foreground = BrushPrgBlue;
        PrgBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        PrgBar.Value = 0;

        LblStepNumber.Text = "Starting...";
        LblStepDetails.Text = "Initializing dump pipeline...";
        _logEntries.Clear();

        _stopwatch.Restart();
        _elapsedTimer.Start();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Action<int, int, string> progressCallback = (step, total, status) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (status.Contains("Dump completed successfully", StringComparison.OrdinalIgnoreCase))
                {
                    PrgBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
                    PrgBar.Value = 100;
                    PrgBar.Foreground = BrushPrgGreen;
                    LblStepNumber.Text = "Finished";
                    LblStepDetails.Text = status;
                    return;
                }

                LblStepNumber.Text = $"Step {step} of {total}";
                LblStepDetails.Text = status;

                // Weighted progress calculation mapping real engine phases
                double percent = step switch
                {
                    1 => 10,
                    2 => 20,
                    3 => 30,
                    4 => 45,
                    5 => status switch
                    {
                        var s when s.Contains("Searching symbols", StringComparison.OrdinalIgnoreCase) => 55,
                        var s when s.Contains("Decompiling", StringComparison.OrdinalIgnoreCase) => 70,
                        var s when s.Contains("struct", StringComparison.OrdinalIgnoreCase) => 85,
                        var s when s.Contains("dummy", StringComparison.OrdinalIgnoreCase) => 95,
                        _ => 50
                    },
                    _ => 0
                };

                SetProgressAnimated(percent);
            });
        };

        Action<string> logCallback = msg =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Detect versions from log stream for status bar display
                if (msg.Contains("[*] Unity Version: ", StringComparison.Ordinal))
                {
                    _detectedUnityVersion = msg.Substring(msg.IndexOf("[*] Unity Version: ") + 19).Trim();
                    UpdateVersionsDisplay();
                }
                else if (msg.Contains("successfully validated for version ", StringComparison.Ordinal))
                {
                    int start = msg.IndexOf("for version ") + 12;
                    int end = msg.IndexOf(' ', start);
                    _detectedMetadataVersion = end > start ? "v" + msg[start..end] : "v" + msg[start..];
                    UpdateVersionsDisplay();
                }

                // Tokenize color for read-only log view: neutral for normal lines, color only for key outcomes
                Brush brush = BrushInfo;
                if (msg.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) || msg.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || msg.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                {
                    brush = BrushError;
                }
                else if (msg.StartsWith("[!]", StringComparison.OrdinalIgnoreCase) || msg.Contains("WARN", StringComparison.OrdinalIgnoreCase))
                {
                    brush = BrushWarn;
                }
                else if (msg.StartsWith("[+]", StringComparison.OrdinalIgnoreCase) || msg.Contains("Dump completed successfully", StringComparison.OrdinalIgnoreCase) || msg.Contains("written.", StringComparison.OrdinalIgnoreCase) || msg.Contains("successfully validated", StringComparison.OrdinalIgnoreCase))
                {
                    brush = BrushSuccess;
                }
                else if (msg.StartsWith("    -", StringComparison.Ordinal))
                {
                    brush = BrushSubtle;
                }
                else
                {
                    brush = BrushInfo;
                }

                // Check if user is scrolled to bottom before adding
                bool autoScroll = _logScrollViewer == null || _logScrollViewer.VerticalOffset >= _logScrollViewer.ScrollableHeight - 16;

                if (_logEntries.Count >= 2500)
                {
                    _logEntries.RemoveAt(0);
                }

                _logEntries.Add(new LogEntry(msg, brush));

                if (autoScroll && _logScrollViewer != null)
                {
                    _logScrollViewer.ScrollToBottom();
                }
            });
        };

        try
        {
            var pipeline = new DumpPipeline(
                gamePath: gamePath,
                outputDir: outputDir,
                forceVersion: forceVersion,
                dumperConfig: dumperConfig,
                progressCallback: progressCallback,
                logCallback: logCallback,
                cancellationToken: token
            );

            var result = await Task.Run(() => pipeline.Run(), token);

            _stopwatch.Stop();
            _elapsedTimer.Stop();

            SetStateDone();
            LblStepNumber.Text = "Finished";
            LblStepDetails.Text = $"Dump written to: {result.OutputDir}";

            PrgBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            PrgBar.Value = 100;
            PrgBar.Foreground = BrushPrgGreen;

            _lastOutputDir = result.OutputDir;
            BtnOpenOutput.Visibility = Visibility.Visible;

            if (!string.IsNullOrEmpty(result.UnityVersion))
            {
                _detectedUnityVersion = result.UnityVersion;
            }
            _detectedMetadataVersion = "v" + result.MetadataVersionUsed;
            UpdateVersionsDisplay();
        }
        catch (OperationCanceledException)
        {
            _stopwatch.Stop();
            _elapsedTimer.Stop();

            SetStateCancelled();
            LblStepNumber.Text = "Cancelled";
            LblStepDetails.Text = "Operation was cancelled by user.";
            PrgBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            PrgBar.Foreground = BrushPrgAmber;
            logCallback("[!] Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            _stopwatch.Stop();
            _elapsedTimer.Stop();

            SetStateError();
            LblStepNumber.Text = "Error";
            LblStepDetails.Text = ex.Message;
            PrgBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            PrgBar.Foreground = BrushPrgRed;
            logCallback($"[x] Exception: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"An error occurred during dump execution:\n\n{ex.Message}", "Dump Error", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnCancel.IsEnabled = false;
        LblStepDetails.Text = "Cancelling...";
    }

    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutputDir) && Directory.Exists(_lastOutputDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastOutputDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, $"Failed to open folder: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion

    #region Log Actions

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logEntries.Count > 0)
        {
            try
            {
                string text = string.Join(Environment.NewLine, _logEntries.Select(entry => entry.RawText));
                Clipboard.SetText(text);
                LblToast.Visibility = Visibility.Visible;
                _toastTimer.Stop();
                _toastTimer.Start();
            }
            catch
            {
            }
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
    }

    #endregion
}
