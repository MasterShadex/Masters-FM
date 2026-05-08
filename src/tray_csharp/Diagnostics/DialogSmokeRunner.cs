// Stage 7.6 STEP 2: DialogSmokeRunner. Activated by --smoke-dialogs arg.
// Cycles through all 6 dialog surfaces X N cycles, captures WS/handles/threads
// after each close + forced GC, and writes results to a JSON file in
// %LOCALAPPDATA%\MastersFM\. Used for pre-code-change baseline (STEP 2) and
// post-code-change regression (STEP 13).
//
// This class is NOT on any hot path and is ONLY reachable when --smoke-dialogs
// is passed. It does not affect normal tray operation.

using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MastersFM.Tray.Dialogs;
using MastersFM.Tray.Services;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Diagnostics;

public sealed class DialogSmokeRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private const string Component = "DialogSmoke";

    public DialogSmokeRunner(IServiceProvider services, ILogger logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task RunAsync(string outputPath)
    {
        _logger.Log($"DialogSmokeRunner.RunAsync begin; output={outputPath}", Component);

        var allResults = new List<DialogSmokeDialogResult>();

        // Allow app to fully settle
        await Task.Delay(1500);
        ForceGc();
        await Task.Delay(500);

        // 6-dialog generic 3-cycle smoke
        allResults.Add(await RunDialogCyclesAsync("Welcome", 3,
            () => OpenWindowAsync<WelcomeWindow>(w =>
            {
                var vm = _services.GetRequiredService<WelcomeViewModel>();
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
            })));

        allResults.Add(await RunDialogCyclesAsync("About", 3,
            () => OpenWindowAsync<WelcomeWindow>(w =>
            {
                var vm = _services.GetRequiredService<WelcomeViewModel>();
                vm.ShowAboutTab = true;
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
            })));

        allResults.Add(await RunDialogCyclesAsync("AudioDevice", 3,
            () => OpenWindowAsync<AudioDeviceWindow>(w =>
            {
                var vm = _services.GetRequiredService<AudioDeviceViewModel>();
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
                _ = vm.RefreshAsync();
            })));

        allResults.Add(await RunDialogCyclesAsync("Platforms", 3,
            () => OpenWindowAsync<PlatformsWindow>(w =>
            {
                var vm = _services.GetRequiredService<PlatformsViewModel>();
                vm.RefreshFromConfig();
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
            })));

        allResults.Add(await RunDialogCyclesAsync("SetupWizard", 3,
            () => OpenWindowAsync<SetupWizardWindow>(w =>
            {
                var vm = _services.GetRequiredService<SetupWizardViewModel>();
                vm.Reset();
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
            })));

        allResults.Add(await RunDialogCyclesAsync("ErrorDialog", 3,
            () => OpenWindowAsync<ErrorDialogWindow>(w =>
            {
                var vm = _services.GetRequiredService<ErrorDialogViewModel>();
                vm.Populate("Smoke test error", "This is a programmatic smoke test error.", null);
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
            })));

        // SetupWizard 5-cycle deep-dive (S2.2)
        var wizardDeepDive = await RunWizardDeepDiveAsync();

        var root = new SmokeResults
        {
            RunTimestamp = DateTime.UtcNow.ToString("o"),
            Label = outputPath.Contains("BASELINE") ? "BASELINE" : "REGRESSION",
            Dialogs = allResults,
            WizardDeepDive = wizardDeepDive
        };

        var dir = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json, new System.Text.UTF8Encoding(false));
        _logger.Log($"Smoke results written to {outputPath}", Component);

        LogSummary(root);

        _logger.Log("DialogSmokeRunner.RunAsync completed", Component);
    }

    private async Task<DialogSmokeDialogResult> RunDialogCyclesAsync(
        string dialogName,
        int cycles,
        Func<Task> openFunc)
    {
        _logger.Log($"{dialogName}: starting {cycles}-cycle smoke", Component);
        ForceGc();
        await Task.Delay(200);

        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var preCycleWs = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1);
        var preCycleHandles = proc.HandleCount;
        var preCycleThreads = proc.Threads.Count;

        _logger.Log($"{dialogName}: pre-cycle WS={preCycleWs}MB handles={preCycleHandles} threads={preCycleThreads}", Component);

        var cycleWs = new double[cycles];
        var cycleHandles = new int[cycles];
        var cycleThreads = new int[cycles];

        for (int i = 0; i < cycles; i++)
        {
            _logger.Log($"{dialogName}: cycle {i + 1}/{cycles} -- open", Component);
            await openFunc();
            await Task.Delay(3000); // window open for 3s -- closing happens inside openFunc after 3s
            _logger.Log($"{dialogName}: cycle {i + 1}/{cycles} -- closed; waiting 3s", Component);
            await Task.Delay(3000);
            ForceGc();
            await Task.Delay(300);

            using var p = Process.GetCurrentProcess();
            p.Refresh();
            cycleWs[i] = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1);
            cycleHandles[i] = p.HandleCount;
            cycleThreads[i] = p.Threads.Count;

            _logger.Log($"{dialogName}: cycle {i + 1}/{cycles} post-close WS={cycleWs[i]}MB handles={cycleHandles[i]} threads={cycleThreads[i]}", Component);
        }

        // Linear regression for WS slope
        double slope = 0;
        if (cycles > 1)
        {
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < cycles; i++)
            {
                sumX += i; sumY += cycleWs[i];
                sumXY += i * cycleWs[i]; sumX2 += i * i;
            }
            var denom = cycles * sumX2 - sumX * sumX;
            if (Math.Abs(denom) > 1e-9) slope = Math.Round((cycles * sumXY - sumX * sumY) / denom, 2);
        }

        var result = new DialogSmokeDialogResult
        {
            Name = dialogName,
            Cycles = cycles,
            PreCycleWsMb = preCycleWs,
            PreCycleHandles = preCycleHandles,
            PreCycleThreads = preCycleThreads,
            PostCloseWsMb = cycleWs,
            PostCloseHandles = cycleHandles,
            PostCloseThreads = cycleThreads,
            WsSlopeMbPerCycle = slope,
            HandleDelta = cycleHandles[cycles - 1] - preCycleHandles,
            ThreadDelta = cycleThreads[cycles - 1] - preCycleThreads,
            Pass = Math.Abs(slope) < 5 && Math.Abs(cycleHandles[cycles - 1] - preCycleHandles) < 10 && Math.Abs(cycleThreads[cycles - 1] - preCycleThreads) < 5
        };

        _logger.Log($"{dialogName}: slope={slope:F2}MB/cycle handles_delta={result.HandleDelta} threads_delta={result.ThreadDelta} PASS={result.Pass}", Component);
        return result;
    }

    private async Task<WizardDeepDiveResult> RunWizardDeepDiveAsync()
    {
        const int cycles = 5;
        _logger.Log($"SetupWizard deep-dive: {cycles} cycles", Component);
        ForceGc();
        await Task.Delay(300);

        using var procPre = Process.GetCurrentProcess();
        procPre.Refresh();
        var preCycleWs = Math.Round(procPre.WorkingSet64 / 1024.0 / 1024.0, 1);
        _logger.Log($"WizardDeepDive: pre-cycle WS={preCycleWs}MB handles={procPre.HandleCount}", Component);

        var gen0Pre = GC.CollectionCount(0);
        var gen1Pre = GC.CollectionCount(1);
        var gen2Pre = GC.CollectionCount(2);

        var cycleData = new WizardCycleMetrics[cycles];
        for (int i = 0; i < cycles; i++)
        {
            _logger.Log($"WizardDeepDive cycle {i + 1}/{cycles}: open", Component);
            await OpenWindowAsync<SetupWizardWindow>(w =>
            {
                var vm = _services.GetRequiredService<SetupWizardViewModel>();
                vm.Reset();
                w.DataContext = vm;
                w.Owner = Application.Current.MainWindow;
            });
            await Task.Delay(3000);
            ForceGc();
            await Task.Delay(300);

            using var p = Process.GetCurrentProcess();
            p.Refresh();
            cycleData[i] = new WizardCycleMetrics
            {
                CycleIndex = i + 1,
                WsMb = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1),
                Handles = p.HandleCount,
                Threads = p.Threads.Count,
                Gen0Collections = GC.CollectionCount(0) - gen0Pre,
                Gen1Collections = GC.CollectionCount(1) - gen1Pre,
                Gen2Collections = GC.CollectionCount(2) - gen2Pre
            };
            _logger.Log($"WizardDeepDive cycle {i + 1}: WS={cycleData[i].WsMb}MB handles={cycleData[i].Handles} gen0+={cycleData[i].Gen0Collections}", Component);
        }

        // Idle 60s after cycle 5
        _logger.Log("WizardDeepDive: idle 60s after cycle 5", Component);
        await Task.Delay(60000);
        ForceGc();
        await Task.Delay(500);

        using var procPost = Process.GetCurrentProcess();
        procPost.Refresh();
        var idle60Ws = Math.Round(procPost.WorkingSet64 / 1024.0 / 1024.0, 1);
        var deltaFromPreCycle = Math.Round(idle60Ws - preCycleWs, 1);
        var wizardLeakCandidate = deltaFromPreCycle > 25;
        _logger.Log($"WizardDeepDive idle-60s WS={idle60Ws}MB delta_from_precycle={deltaFromPreCycle}MB LEAK_CANDIDATE={wizardLeakCandidate}", Component);

        return new WizardDeepDiveResult
        {
            PreCycleWsMb = preCycleWs,
            CycleData = cycleData,
            Idle60sWsMb = idle60Ws,
            DeltaFromPreCycleMb = deltaFromPreCycle,
            WizardLeakCandidate = wizardLeakCandidate
        };
    }

    private async Task OpenWindowAsync<TWindow>(Action<TWindow> configure)
        where TWindow : Window
    {
        // Resolve a fresh transient window instance from DI
        var window = _services.GetRequiredService<TWindow>();
        var closeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            configure(window);
            window.Closed += (_, _) => closeTcs.TrySetResult(true);
            window.Show();

            // Auto-close after 3s via DispatcherTimer
            var closeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                try { window.Close(); } catch { closeTcs.TrySetResult(true); }
            };
            closeTimer.Start();
        });

        // Wait until closed (max 8s safety guard)
        var timeoutTask = Task.Delay(8000);
        await Task.WhenAny(closeTcs.Task, timeoutTask);
        // Force close if timed out
        if (!closeTcs.Task.IsCompleted)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try { window.Close(); } catch { }
            });
        }
    }

    private static void ForceGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
    }

    private void LogSummary(SmokeResults root)
    {
        _logger.Log("=== DIALOG SMOKE SUMMARY ===", Component);
        foreach (var d in root.Dialogs)
        {
            _logger.Log($"  {d.Name}: slope={d.WsSlopeMbPerCycle:F2}MB/cycle handles_delta={d.HandleDelta} threads_delta={d.ThreadDelta} PASS={d.Pass}", Component);
        }
        var wd = root.WizardDeepDive;
        _logger.Log($"  WizardDeepDive: idle60s_delta={wd.DeltaFromPreCycleMb:F1}MB LEAK_CANDIDATE={wd.WizardLeakCandidate}", Component);
    }

    // -- Result DTOs --

    public sealed class SmokeResults
    {
        public string RunTimestamp { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public List<DialogSmokeDialogResult> Dialogs { get; set; } = new();
        public WizardDeepDiveResult WizardDeepDive { get; set; } = new();
    }

    public sealed class DialogSmokeDialogResult
    {
        public string Name { get; set; } = string.Empty;
        public int Cycles { get; set; }
        public double PreCycleWsMb { get; set; }
        public int PreCycleHandles { get; set; }
        public int PreCycleThreads { get; set; }
        public double[] PostCloseWsMb { get; set; } = Array.Empty<double>();
        public int[] PostCloseHandles { get; set; } = Array.Empty<int>();
        public int[] PostCloseThreads { get; set; } = Array.Empty<int>();
        public double WsSlopeMbPerCycle { get; set; }
        public int HandleDelta { get; set; }
        public int ThreadDelta { get; set; }
        public bool Pass { get; set; }
    }

    public sealed class WizardDeepDiveResult
    {
        public double PreCycleWsMb { get; set; }
        public WizardCycleMetrics[] CycleData { get; set; } = Array.Empty<WizardCycleMetrics>();
        public double Idle60sWsMb { get; set; }
        public double DeltaFromPreCycleMb { get; set; }
        public bool WizardLeakCandidate { get; set; }
    }

    public sealed class WizardCycleMetrics
    {
        public int CycleIndex { get; set; }
        public double WsMb { get; set; }
        public int Handles { get; set; }
        public int Threads { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
    }
}
