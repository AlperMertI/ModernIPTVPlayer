using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace MpvWinUI.Common;

public class PerformanceProfiler
{
    private class StepInfo
    {
        public List<double> Times = new();
        public Stopwatch Stopwatch = new();
    }

    private readonly Stopwatch _frameStopwatch = new();
    private static readonly Stopwatch _globalStopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, StepInfo> _steps = new();
    private readonly List<double> _totalTimes = new();

    public static void LogEvent(string message)
    {
        Debug.WriteLine($"[Event][{_globalStopwatch.Elapsed.TotalMilliseconds:F2}ms] {message}");
    }
    
    private DateTime _lastLogTime = DateTime.Now;
    private int _frameCount = 0;

    public void BeginFrame()
    {
        _frameStopwatch.Restart();
        _frameCount++;
    }

    public void BeginStep(string name)
    {
        if (!_steps.ContainsKey(name))
            _steps[name] = new StepInfo();
        
        _steps[name].Stopwatch.Restart();
    }

    public void EndStep(string name)
    {
        if (_steps.TryGetValue(name, out var info))
        {
            info.Stopwatch.Stop();
            info.Times.Add(info.Stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public void EndFrame()
    {
        _frameStopwatch.Stop();
        _totalTimes.Add(_frameStopwatch.Elapsed.TotalMilliseconds);

        if ((DateTime.Now - _lastLogTime).TotalSeconds >= 2)
        {
            LogMetrics();
            Reset();
        }
    }

    private void Reset()
    {
        _lastLogTime = DateTime.Now;
        foreach (var step in _steps.Values) step.Times.Clear();
        _totalTimes.Clear();
        _frameCount = 0;
    }

    public void RecordAsyncStep(string name, double milliseconds)
    {
        if (!_steps.ContainsKey(name))
            _steps[name] = new StepInfo();
        
        lock (_steps[name].Times)
        {
            _steps[name].Times.Add(milliseconds);
        }
    }

    private void LogMetrics()
    {
        if (_frameCount == 0) return;

        double avgTotal = _totalTimes.DefaultIfEmpty(0).Average();
        double fps = _frameCount / (DateTime.Now - _lastLogTime).TotalSeconds;

        var stepResults = new List<string>();
        foreach (var step in _steps)
        {
            double avg;
            lock (step.Value.Times)
            {
                avg = step.Value.Times.DefaultIfEmpty(0).Average();
            }
            stepResults.Add($"{step.Key}: {avg:F2}ms");
        }

        string metrics = string.Join(" | ", stepResults);
        
        Debug.WriteLine($"[Tracer] FPS: {fps:F1} | Total: {avgTotal:F2}ms | {metrics}");
    }
}
