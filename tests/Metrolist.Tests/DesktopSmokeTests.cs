using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Metrolist.Desktop;
using Metrolist.Desktop.ViewModels;

namespace Metrolist.Tests;

public static class DesktopSmokeTests
{
    public static void Run(bool software = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunWindow(software); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunWindow(bool software)
    {
        var startup = Stopwatch.StartNew();
        var directory = Path.GetFullPath(Path.Combine("artifacts", "desktop-smoke-" + Guid.NewGuid().ToString("N")));
        var builder = Metrolist.Desktop.Program.BuildAvaloniaApp();
        if (software) builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        builder.SetupWithoutStarting();
        var viewModel = new MainViewModel(directory);
        var window = new MainWindow(viewModel) { ShowActivated = false };
        using var loop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string audioFile = Path.Combine(directory, "native-playback-test.wav");
        Directory.CreateDirectory(directory);
        using (var audio = new BinaryWriter(File.Create(audioFile)))
        {
            const int bytes = 44100 * 2 * 2 * 60;
            audio.Write("RIFF"u8); audio.Write(bytes + 36); audio.Write("WAVEfmt "u8);
            audio.Write(16); audio.Write((short)1); audio.Write((short)2); audio.Write(44100);
            audio.Write(44100 * 4); audio.Write((short)4); audio.Write((short)16);
            audio.Write("data"u8); audio.Write(bytes); audio.BaseStream.SetLength(bytes + 44L);
        }
        window.Closed += (_, _) => loop.Cancel();
        window.Opened += (_, _) => Console.WriteLine($"DESKTOP startup to window opened: {startup.Elapsed.TotalMilliseconds:F0} ms");
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        var interval = Stopwatch.StartNew();
        int phase = 0;
        bool completed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) =>
        {
            process.Refresh();
            double cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / interval.Elapsed.TotalSeconds * 100;
            string state = phase switch { 1 => "visible idle", 2 => "visible WAV playback", 3 => "visible paused", 4 => "minimized WAV playback", _ => "minimized paused" };
            if (phase > 0)
                Console.WriteLine($"DESKTOP {state}: CPU={cpu:F3}% one core ({cpu / Environment.ProcessorCount:F3}% host), private={process.PrivateMemorySize64 / 1048576d:F2} MiB, working={process.WorkingSet64 / 1048576d:F2} MiB, managed={GC.GetTotalMemory(false) / 1048576d:F2} MiB, threads={process.Threads.Count}");
            if (phase++ < 5)
            {
                if (phase == 2)
                {
                    timer.Stop();
                    viewModel.Volume = 0;
                    await viewModel.OpenLocalFilesAsync([audioFile]);
                    await Task.Delay(1000);
                    timer.Start();
                }
                else if (phase == 3) viewModel.Player.Pause();
                else if (phase == 4) { viewModel.Player.Play(); window.WindowState = WindowState.Minimized; }
                else if (phase == 5) viewModel.Player.Pause();
                cpuStart = process.TotalProcessorTime;
                interval.Restart();
            }
            else
            {
                timer.Stop();
                completed = true;
                window.Close();
            }
        };
        try
        {
            window.Show();
            cpuStart = process.TotalProcessorTime;
            interval.Restart();
            timer.Start();
            Dispatcher.UIThread.MainLoop(loop.Token);
            if (!completed) throw new TimeoutException("The native window did not close cleanly within the smoke-test deadline.");
            Console.WriteLine("PASS native Win32 startup, visible/minimized playback and resource samples, clean close");
        }
        finally
        {
            timer.Stop();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
