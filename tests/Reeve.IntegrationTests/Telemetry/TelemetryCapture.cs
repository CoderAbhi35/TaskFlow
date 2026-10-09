using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Reeve.Application.Telemetry;

namespace Reeve.IntegrationTests.Telemetry;

/// <summary>
/// Records Reeve spans and metric measurements in-process, the way an OpenTelemetry SDK would
/// receive them, so tests can assert on trace structure and metric values without a collector.
/// </summary>
public sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();

    public ConcurrentBag<Activity> Spans { get; } = [];
    public ConcurrentBag<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReeveTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Spans.Add,
        };
        ActivitySource.AddActivityListener(_activities);

        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ReeveTelemetry.Name)
                listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _meters.Start();
    }

    /// <summary>Sum of a counter's measurements whose tags include all of <paramref name="tags"/>.</summary>
    public double Sum(string instrument, params (string Key, object Value)[] tags) =>
        Measurements
            .Where(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
            .Sum(m => m.Value);

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        Measurements.Add((instrument.Name, value, dict));
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }
}
