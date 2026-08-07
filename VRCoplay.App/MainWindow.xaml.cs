using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vanara.PInvoke;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization.NumberFormatting;
using static Vanara.PInvoke.DwmApi;
using WinRT.Interop;

namespace VRCoplay;

public sealed partial class MainWindow : Window
{
    private const string Url = "rtsp://127.0.0.1:8554/game";
    private const int SampleRate = 44100, AudioChunkBytes = SampleRate * 2 * 4 / 100;
    private static readonly (string Name, string[] Args, bool Cpu)[] Encoders =
    [
        ("NVIDIA NVENC", ["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "constqp", "-qp", "25", "-forced-idr", "1", "-zerolatency", "1", "-delay", "0"], false),
        ("AMD AMF", ["-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed", "-latency", "1", "-async_depth", "1", "-preanalysis", "0", "-preencode", "0", "-enforce_hrd", "1", "-filler_data", "0", "-vbaq", "0", "-rc", "cbr", "-b:v", "8M", "-maxrate", "8M", "-bufsize", "133k", "-profile:v", "constrained_baseline", "-forced_idr", "1"], false),
        ("Intel Quick Sync", ["-c:v", "h264_qsv", "-preset", "veryfast", "-async_depth", "1", "-scenario", "remotegaming", "-global_quality", "25", "-profile:v", "baseline", "-forced_idr", "1"], true),
        ("x264 software", ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-qp", "25", "-profile:v", "baseline"], true)
    ];

    private sealed record WindowChoice(int Pid, nint Hwnd, string Label);
    private sealed record StreamSettings(bool Audio = true, bool Controller = true, int World = 0, bool Cursor = true, int? Fps = null, int? Width = null, int? Height = null,
        int CropLeft = 0, int CropTop = 0, int CropRight = 0, int CropBottom = 0, int Encoder = 0, int? Quality = null, string Extra = "");

    private (string Name, string[] Args, bool Cpu)? _encoder;
    private WindowChoice? _target;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _ffmpeg, _server;
    private WasapiRecorder? _recorder;
    private VrXInputBridge? _input;
    private HTHUMBNAIL _thumbnail;
    private string _lastLine = "";

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.ResizeClient(new((int)(820 * scale), (int)(720 * scale)));
        foreach (var box in (NumberBox[])[FpsBox, WidthBox, HeightBox, CropLeftBox, CropTopBox, CropRightBox, CropBottomBox, QualityBox])
            box.NumberFormatter = new DecimalFormatter { FractionDigits = 0, NumberRounder = new IncrementNumberRounder { Increment = 1 } };
        LoadSettings();
        RefreshWindows();
        PreviewHost.Loaded += (_, _) => ShowPreview(); RootPage.SizeChanged += (_, _) => { Hero.MinHeight = Scroller.ActualHeight; DispatcherQueue.TryEnqueue(UpdatePreview); };
        Scroller.ViewChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePreview);
        Closed += (_, _) => { try { SaveSettings(ReadSettings()); } catch { } ClosePreview(); StopNow(); };
    }

    private void WindowPicker_DropDownOpened(object sender, object e) => RefreshWindows();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();
    private void EncoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => _encoder = null;

    private void CopyUrl_Click(object sender, RoutedEventArgs e) { try {
        var data = new DataPackage(); data.SetText(Url); var copied = Clipboard.SetContentWithOptions(data, new());
        Show(copied ? "Stream URL copied." : "Clipboard is busy; try again.", copied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    } catch (Exception ex) { Show($"Could not copy: {ex.Message}", InfoBarSeverity.Warning); } }
    private void Reset_Click(object sender, RoutedEventArgs e) { var s = new StreamSettings(); ApplySettings(s); SaveSettings(s); Show("Settings reset to automatic.", InfoBarSeverity.Success); }

    private void RefreshWindows()
    {
        var previous = WindowPicker.SelectedItem as WindowChoice;
        var windows = new List<WindowChoice>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                process.Refresh();
                var title = process.MainWindowTitle.Trim();
                if (process.Id != Environment.ProcessId && process.MainWindowHandle != 0 && title.Length > 0)
                    windows.Add(new(process.Id, process.MainWindowHandle, $"{title} - {process.ProcessName} ({process.Id})"));
            }
            catch { }
            finally { process.Dispose(); }
        }
        var items = windows.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        WindowPicker.ItemsSource = items;
        WindowPicker.SelectedItem = items.FirstOrDefault(x => x.Pid == previous?.Pid && x.Hwnd == previous.Hwnd)
                                    ?? items.FirstOrDefault();
    }

    private void WindowPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowPreview();
        if (_cts is null || WindowPicker.SelectedItem is not WindowChoice next || IsCurrent(next)) return;
        _target = next; Show("Switching application...", InfoBarSeverity.Informational);
        try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(true); } catch { }
    }

    private void ShowPreview()
    {
        // Look at https://learn.microsoft.com/windows/win32/dwm/thumbnail-ovw - DWM owns the live copy.
        ClosePreview();
        if (WindowPicker.SelectedItem is not WindowChoice source || PreviewHost.ActualWidth == 0 ||
            DwmRegisterThumbnail(WindowNative.GetWindowHandle(this), source.Hwnd, out _thumbnail).Failed) return;
        PreviewEmpty.Visibility = Visibility.Collapsed; UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_thumbnail.IsNull || DwmQueryThumbnailSourceSize(_thumbnail, out var source).Failed) return;
        var scale = PreviewHost.XamlRoot.RasterizationScale; var at = PreviewHost.TransformToVisual(null).TransformPoint(new());
        var availableWidth = PreviewHost.ActualWidth * scale; var availableHeight = PreviewHost.ActualHeight * scale;
        var fit = Math.Min(availableWidth / source.cx, availableHeight / source.cy);
        var width = (int)(source.cx * fit); var height = (int)(source.cy * fit);
        var left = (int)(at.X * scale + (availableWidth - width) / 2); var top = (int)(at.Y * scale + (availableHeight - height) / 2);
        var properties = new DWM_THUMBNAIL_PROPERTIES {
            dwFlags = DWM_TNP.DWM_TNP_RECTDESTINATION | DWM_TNP.DWM_TNP_VISIBLE | DWM_TNP.DWM_TNP_OPACITY,
            rcDestination = new(left, top, left + width, top + height), fVisible = true, opacity = 255 };
        DwmUpdateThumbnailProperties(_thumbnail, properties);
    }

    private void ClosePreview() { if (!_thumbnail.IsNull) DwmUnregisterThumbnail(_thumbnail); _thumbnail = default; PreviewEmpty.Visibility = Visibility.Visible; }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            if (_cts is null) await StartAsync();
            else
            {
                Show("Stopping...", InfoBarSeverity.Informational);
                var loop = _loop; _cts.Cancel();
                if (loop is not null) await loop; else StopNow();
            }
        }
        catch (Exception ex)
        {
            StopNow(); SetStreamingControls(false);
            Show($"Could not start: {ex.GetBaseException().Message}", InfoBarSeverity.Error);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private async Task StartAsync()
    {
        if (WindowPicker.SelectedItem is not WindowChoice target)
        {
            Show("Choose an application first.", InfoBarSeverity.Warning);
            return;
        }

        var settings = ReadSettings(); ValidateSettings(settings);
        SetStreamingControls(true);
        Show("Checking the encoder and starting the local server...", InfoBarSeverity.Informational);
        EnsurePortFree();
        _encoder ??= await PickEncoderAsync(settings.Encoder);
        _target = target;
        _cts = new CancellationTokenSource();
        _server = StartTool(Tool("mediamtx.exe"), [Tool("mediamtx.yml")]);
        for (var waited = 0; !PortInUse(); waited += 50)
        {
            if (_server.HasExited || waited >= 5000) throw new InvalidOperationException($"MediaMTX did not start. {_lastLine}");
            await Task.Delay(50, _cts.Token);
        }
        _input = settings.Controller ? new VrXInputBridge() : null;
        SaveSettings(settings);
        _loop = StreamLoopAsync(settings, _cts.Token);
    }

    private async Task StreamLoopAsync(StreamSettings settings, CancellationToken stop)
    {
        var final = "Stopped."; var severity = InfoBarSeverity.Success;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var target = _target ?? throw new InvalidOperationException("Choose an application first.");
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stop);
                WasapiRecorder? recorder = null;
                Task pump = Task.Delay(Timeout.Infinite, iteration.Token);
                try
                {
                    var hwnd = CurrentHandle(target.Pid);
                    BufferedWaveProvider? buffer = null;
                    var audioFault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (settings.Audio)
                    {
                        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
                        buffer = new(format, TimeSpan.FromMilliseconds(40)) { DiscardOnBufferOverflow = true };
                        // Look at https://github.com/naudio/NAudio/blob/6def00b5a41a7904f3b104eda8f92a1c59be7e5a/src/NAudio.Wasapi/WasapiRecorderBuilder.cs
                        recorder = await new WasapiRecorderBuilder().WithProcessLoopback((uint)target.Pid,
                            ProcessLoopbackMode.IncludeTargetProcessTree).WithFormat(format).WithBufferLength(20)
                            .WithMmcssThreadPriority().BuildAsync();
                        recorder.DataAvailable += (data, _, _, _) => buffer.AddSamples(data);
                        recorder.RecordingStopped += (_, e) => { if (e.Exception is not null) audioFault.TrySetResult(e.Exception); };
                        _recorder = recorder;
                    }

                    _ffmpeg = StartTool(Tool("ffmpeg.exe"), FfmpegArgs(hwnd, settings), settings.Audio);
                    if (recorder is not null)
                    {
                        recorder.StartRecording();
                        pump = Task.Run(() => PumpAudioAsync(buffer!, _ffmpeg.StandardInput.BaseStream, iteration.Token));
                    }
                    Show($"Streaming with {_encoder!.Value.Name}. Paste {Url} into VRChat.", InfoBarSeverity.Success);

                    using var watched = Process.GetProcessById(target.Pid);
                    var ffmpegExit = _ffmpeg.WaitForExitAsync(iteration.Token);
                    var targetExit = watched.WaitForExitAsync(iteration.Token);
                    var ended = await Task.WhenAny(ffmpegExit, targetExit, pump, audioFault.Task);
                    if (ended == targetExit && IsCurrent(target)) { final = "The selected application closed."; break; }
                    if (ended == audioFault.Task && IsCurrent(target)) throw await audioFault.Task;
                    if (ended == pump && IsCurrent(target)) await pump;
                }
                finally
                {
                    iteration.Cancel();
                    if (recorder is not null) await recorder.DisposeAsync();
                    _recorder = null;
                    try { _ffmpeg?.StandardInput.Close(); } catch { }
                    End(_ffmpeg); _ffmpeg = null;
                    try { await pump; } catch { }
                }
                if (!stop.IsCancellationRequested)
                {
                    if (IsCurrent(target))
                        Show($"Capture interrupted; restarting... {_lastLine}", InfoBarSeverity.Warning);
                    await Task.Delay(250, stop);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            final = $"Streaming stopped: {ex.GetBaseException().Message}";
            severity = InfoBarSeverity.Error;
        }
        finally
        {
            _input?.Dispose(); _input = null;
            End(_server); _server = null;
            _cts?.Dispose(); _cts = null; _loop = null;
            SetStreamingControls(false);
            Show(final, severity);
        }
    }

    private async Task PumpAudioAsync(BufferedWaveProvider buffer, Stream output, CancellationToken stop)
    {
        var bytes = new byte[AudioChunkBytes];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(stop))
        {
            buffer.Read(bytes);
            await output.WriteAsync(bytes, stop);
        }
    }

    private async Task<(string Name, string[] Args, bool Cpu)> PickEncoderAsync(int choice)
    {
        foreach (var profile in choice == 0 ? Encoders : Encoders.Skip(choice - 1).Take(1))
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256:rate=1,format=nv12", "-frames:v", "1", "-an" };
            args.AddRange(profile.Args); args.AddRange(["-f", "null", "NUL"]);
            using var probe = StartTool(Tool("ffmpeg.exe"), args);
            try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { End(probe); continue; }
            if (probe.ExitCode == 0) return profile;
        }
        throw new InvalidOperationException($"{(choice == 0 ? "No H.264 encoder is" : $"{Encoders[choice - 1].Name} is not")} available. {_lastLine}");
    }

    private IEnumerable<string> FfmpegArgs(nint hwnd, StreamSettings settings)
    {
        var capture = $"gfxcapture=hwnd={unchecked((ulong)hwnd.ToInt64())}";
        if (!settings.Cursor) capture += ":capture_cursor=0";
        if (settings.Fps is int fps) capture += $":max_framerate={fps}";
        if (settings.CropLeft + settings.CropTop + settings.CropRight + settings.CropBottom > 0) capture += $":crop_left={settings.CropLeft}:crop_top={settings.CropTop}:crop_right={settings.CropRight}:crop_bottom={settings.CropBottom}";
        if (settings.Width is int width) capture += $":width={width}:height={settings.Height}:resize_mode=scale_aspect";
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-xerror", "-f", "lavfi", "-i", capture };
        if (settings.Audio) args.AddRange(["-f", "f32le", "-ar", SampleRate.ToString(), "-ac", "2", "-i", "pipe:0", "-map", "0:v:0", "-map", "1:a:0"]);
        else args.Add("-an");
        if (settings.World == 2) args.AddRange(["-vf", "hwdownload,format=bgra,drawtext=fontfile='C\\:/Windows/Fonts/seguisb.ttf':text='HIGH LATENCY - WORLD SETTING OFF':fontcolor=0xFFD54A:fontsize=28:box=1:boxcolor=black@0.72:boxborderw=12:x=w-tw-24:y=h-th-24,format=nv12"]);
        else if (_encoder!.Value.Cpu) args.AddRange(["-vf", "hwdownload,format=bgra,format=nv12"]);
        args.AddRange(EncoderArgs(_encoder!.Value, settings.Quality));
        args.AddRange(["-g", (settings.Fps is int rate ? Math.Max(1, (rate + 2) / 4) : 15).ToString(), "-bf", "0", "-refs", "1", "-force_key_frames", "expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+0.25))", "-fps_mode", "vfr", "-flush_packets", "1"]);
        if (settings.Audio) args.AddRange(["-c:a", "pcm_s16be", "-ar", SampleRate.ToString(), "-ac", "2"]);
        args.AddRange(SplitArgs(settings.Extra));
        args.AddRange(["-f", "rtsp", "-rtsp_transport", "tcp", "-pkt_size", "1200", Url]);
        return args;
    }

    private static IEnumerable<string> EncoderArgs((string Name, string[] Args, bool Cpu) profile, int? quality)
    {
        if (quality is null) return profile.Args;
        var args = profile.Args.ToList();
        if (profile.Name == "AMD AMF") {
            foreach (var key in new[] { "-rc", "-b:v", "-maxrate", "-bufsize" }) { var i = args.IndexOf(key); args.RemoveRange(i, 2); }
            args.AddRange(["-rc", "cqp", "-qp_i", quality.Value.ToString(), "-qp_p", quality.Value.ToString()]);
        } else { var key = profile.Name == "Intel Quick Sync" ? "-global_quality" : "-qp"; args[args.IndexOf(key) + 1] = quality.Value.ToString(); }
        return args;
    }

    private static string[] SplitArgs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var argv = CommandLineToArgvW($"vrcoplay {text}", out var count);
        if (argv == 0) throw new InvalidOperationException("Could not parse the extra FFmpeg arguments.");
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    private StreamSettings ReadSettings() => new(AudioToggle.IsOn, ControllerToggle.IsOn, WorldMode.SelectedIndex, CursorToggle.IsOn, Number(FpsBox),
        Number(WidthBox), Number(HeightBox), Number(CropLeftBox) ?? 0, Number(CropTopBox) ?? 0, Number(CropRightBox) ?? 0,
        Number(CropBottomBox) ?? 0, EncoderPicker.SelectedIndex, Number(QualityBox), ExtraArgs.Text);

    // ponytail: the DecimalFormatter above rounds input to integers, so no whole-number validation is needed here.
    private static int? Number(NumberBox box) => double.IsNaN(box.Value) ? null : (int)Math.Round(box.Value);

    private void ValidateSettings(StreamSettings s)
    {
        // ponytail: ranges are enforced by NumberBox Minimum/Maximum in XAML; only cross-field rules live here.
        if ((s.Width is null) != (s.Height is null)) throw new InvalidOperationException("Set both output width and height, or leave both automatic.");
        if (s.Width is int width && (width % 2 != 0 || s.Height % 2 != 0)) throw new InvalidOperationException("Output width and height must be even numbers.");
        if (!_thumbnail.IsNull && DwmQueryThumbnailSourceSize(_thumbnail, out var source).Succeeded && (s.CropLeft + s.CropRight >= source.cx || s.CropTop + s.CropBottom >= source.cy)) throw new InvalidOperationException("Crop values must leave part of the source image visible.");
        SplitArgs(s.Extra);
    }

    private void ApplySettings(StreamSettings s) {
        AudioToggle.IsOn = s.Audio; ControllerToggle.IsOn = s.Controller; WorldMode.SelectedIndex = s.World; CursorToggle.IsOn = s.Cursor;
        FpsBox.Value = s.Fps ?? double.NaN; WidthBox.Value = s.Width ?? double.NaN; HeightBox.Value = s.Height ?? double.NaN;
        CropLeftBox.Value = s.CropLeft; CropTopBox.Value = s.CropTop; CropRightBox.Value = s.CropRight; CropBottomBox.Value = s.CropBottom;
        EncoderPicker.SelectedIndex = s.Encoder; QualityBox.Value = s.Quality ?? double.NaN; ExtraArgs.Text = s.Extra;
    }

    // ponytail: Debug builds run unpackaged (WindowsPackageType=None), where GetDefault throws for lack of package identity.
    private static Microsoft.Windows.Storage.ApplicationData Store() {
        try { return Microsoft.Windows.Storage.ApplicationData.GetDefault(); }
        catch { return Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged("VRCoplay", "VRCoplay"); }
    }

    private void LoadSettings() {
        try {
            ApplySettings(Store().LocalSettings.Values.TryGetValue("streamSettings", out var value) && value is string json
                ? JsonSerializer.Deserialize<StreamSettings>(json) ?? new() : new());
        } catch { ApplySettings(new()); }
    }

    private static void SaveSettings(StreamSettings settings) =>
        Store().LocalSettings.Values["streamSettings"] = JsonSerializer.Serialize(settings);

    private Process StartTool(string path, IEnumerable<string> args, bool stdin = false)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = stdin, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(path)}.");
        DataReceivedEventHandler remember = (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _lastLine = e.Data.Trim(); };
        process.OutputDataReceived += remember; process.ErrorDataReceived += remember;
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return process;
    }

    private static nint CurrentHandle(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle == 0)
            throw new InvalidOperationException("The selected application no longer has a window.");
        if (IsIconic(process.MainWindowHandle) != 0)
            throw new InvalidOperationException("Restore the selected application before streaming.");
        return process.MainWindowHandle;
    }

    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-isiconic
    [DllImport("user32.dll")] private static extern int IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    private static bool PortInUse() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 8554);

    private static void EnsurePortFree()
    {
        if (PortInUse()) throw new InvalidOperationException("Port 8554 is already in use. Stop the other stream first.");
    }

    private void StopNow()
    {
        _cts?.Cancel();
        try { _recorder?.Dispose(); } catch { }
        try { _ffmpeg?.StandardInput.Close(); } catch { }
        End(_ffmpeg); End(_server); _ffmpeg = _server = null;
        _input?.Dispose(); _input = null;
        _recorder = null; _cts?.Dispose(); _cts = null; _loop = null;
    }

    private static void End(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } } catch { }
        process.Dispose();
    }

    private void SetStreamingControls(bool streaming)
    {
        SettingsSheet.IsEnabled = !streaming;
        StartButton.Content = streaming ? "Stop streaming" : "Start streaming";
    }

    private bool IsCurrent(WindowChoice target) => _target?.Pid == target.Pid && _target.Hwnd == target.Hwnd;

    private void Show(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true;
    }

    private static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, "Tools", name);
}
