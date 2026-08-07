using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vanara.PInvoke;
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
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new(820, 720));
        RefreshWindows();
        PreviewHost.Loaded += (_, _) => ShowPreview(); RootPage.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePreview);
        Scroller.ViewChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePreview);
        Closed += (_, _) => { ClosePreview(); StopNow(); };
    }

    private void WindowPicker_DropDownOpened(object sender, object e) => RefreshWindows();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

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

        SetStreamingControls(true);
        Show("Checking the encoder and starting the local server...", InfoBarSeverity.Informational);
        EnsurePortFree();
        _encoder ??= await PickEncoderAsync();
        _target = target;
        _cts = new CancellationTokenSource();
        _server = StartTool(Tool("mediamtx.exe"), [Tool("mediamtx.yml")]);
        await Task.Delay(500, _cts.Token);
        if (_server.HasExited) throw new InvalidOperationException($"MediaMTX stopped. {_lastLine}");
        _input = new VrXInputBridge();
        _loop = StreamLoopAsync(AudioToggle.IsOn, LowLatencyToggle.IsOn, _cts.Token);
    }

    private async Task StreamLoopAsync(bool includeAudio, bool lowLatency, CancellationToken stop)
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
                    if (includeAudio)
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

                    _ffmpeg = StartTool(Tool("ffmpeg.exe"), FfmpegArgs(hwnd, includeAudio, lowLatency), includeAudio);
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

    private async Task<(string Name, string[] Args, bool Cpu)> PickEncoderAsync()
    {
        foreach (var profile in Encoders)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256:rate=1,format=nv12", "-frames:v", "1", "-an" };
            args.AddRange(profile.Args); args.AddRange(["-f", "null", "NUL"]);
            using var probe = StartTool(Tool("ffmpeg.exe"), args);
            try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { End(probe); continue; }
            if (probe.ExitCode == 0) return profile;
        }
        throw new InvalidOperationException($"No H.264 encoder is available. {_lastLine}");
    }

    private IEnumerable<string> FfmpegArgs(nint hwnd, bool audio, bool lowLatency)
    {
        var capture = $"gfxcapture=hwnd={unchecked((ulong)hwnd.ToInt64())}";
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-xerror", "-f", "lavfi", "-i", capture };
        if (audio) args.AddRange(["-f", "f32le", "-ar", SampleRate.ToString(), "-ac", "2", "-i", "pipe:0", "-map", "0:v:0", "-map", "1:a:0"]);
        else args.Add("-an");
        if (!lowLatency)
            args.AddRange(["-vf", "hwdownload,format=bgra,drawtext=fontfile='C\\:/Windows/Fonts/seguisb.ttf':text='HIGH LATENCY - WORLD SETTING OFF':fontcolor=0xFFD54A:fontsize=28:box=1:boxcolor=black@0.72:boxborderw=12:x=w-tw-24:y=h-th-24,format=nv12"]);
        else if (_encoder!.Value.Cpu) args.AddRange(["-vf", "hwdownload,format=bgra,format=nv12"]);
        args.AddRange(_encoder!.Value.Args);
        args.AddRange(["-g", "15", "-bf", "0", "-refs", "1", "-force_key_frames", "expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+0.25))", "-fps_mode", "vfr", "-flush_packets", "1"]);
        if (audio) args.AddRange(["-c:a", "pcm_s16be", "-ar", SampleRate.ToString(), "-ac", "2"]);
        args.AddRange(["-f", "rtsp", "-rtsp_transport", "tcp", "-pkt_size", "1200", Url]);
        return args;
    }

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

    private static void EnsurePortFree()
    {
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 8554))
            throw new InvalidOperationException("Port 8554 is already in use. Stop the other stream first.");
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
        AudioToggle.IsEnabled = LowLatencyToggle.IsEnabled = !streaming;
        StartButton.Content = streaming ? "Stop streaming" : "Start streaming";
    }

    private bool IsCurrent(WindowChoice target) => _target?.Pid == target.Pid && _target.Hwnd == target.Hwnd;

    private void Show(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true;
    }

    private static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, "Tools", name);
}
