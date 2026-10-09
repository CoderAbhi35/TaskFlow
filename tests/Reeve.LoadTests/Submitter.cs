using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Reeve.LoadTests;

/// <summary>
/// Submits jobs through <c>POST /api/v1/jobs</c>, either as fast as <c>--concurrency</c> connections
/// allow or at a fixed <c>--rate</c> per second, and records every request's latency and status.
/// Every payload carries the run ID, so the run's jobs can be found in the database afterwards.
/// </summary>
internal sealed class Submitter(Options options, string runId)
{
    private readonly string _baseUrl = options.Get("base-url", "http://localhost:8080").TrimEnd('/');
    private readonly int _jobs = options.GetInt("jobs", 1000);
    private readonly int _concurrency = options.GetInt("concurrency", 32);
    private readonly double _rate = options.GetDouble("rate", 0);
    private readonly string _type = options.Get("type", "GENERATE_REPORT");
    private readonly int _durationMs = options.GetInt("duration-ms", 0);

    public async Task<SubmissionResult> RunAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = _concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync(http, cancellationToken));

        var latencies = new double[_jobs];
        var statuses = new int[_jobs];
        var next = -1;
        var clock = Stopwatch.StartNew();

        async Task SendLoopAsync()
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < _jobs && !cancellationToken.IsCancellationRequested)
            {
                if (_rate > 0)
                {
                    // Open-loop pacing: request i is due at i / rate, whether or not earlier ones were slow.
                    var due = TimeSpan.FromSeconds(i / _rate) - clock.Elapsed;
                    if (due > TimeSpan.Zero)
                        await Task.Delay(due, cancellationToken);
                }

                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var response = await http.PostAsync("/api/v1/jobs", Body(i), cancellationToken);
                    statuses[i] = (int)response.StatusCode;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    statuses[i] = -1; // connection failure or timeout
                }
                latencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        var startedAt = DateTimeOffset.UtcNow;
        await Task.WhenAll(Enumerable.Range(0, _concurrency).Select(_ => Task.Run(SendLoopAsync, cancellationToken)));
        var elapsed = clock.Elapsed;

        var sent = Math.Min(next, _jobs);
        var sorted = latencies.Take(sent).Order().ToArray();
        return new SubmissionResult
        {
            StartedAt = startedAt,
            Requests = sent,
            Seconds = Math.Round(elapsed.TotalSeconds, 2),
            RequestsPerSecond = Math.Round(sent / elapsed.TotalSeconds, 1),
            Accepted = statuses.Count(s => s is 200 or 201),
            StatusCounts = statuses.Take(sent).GroupBy(s => s).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            LatencyMs = Percentiles.Of(sorted),
        };
    }

    private StringContent Body(int i)
    {
        var simulate = new Dictionary<string, object> { ["durationMs"] = _durationMs };
        var payload = new Dictionary<string, object> { ["run"] = runId, ["simulate"] = simulate };
        // The sample handlers validate their payload: give each type the field it requires.
        switch (_type)
        {
            case "SEND_NOTIFICATION": payload["userId"] = i; break;
            case "PROCESS_IMAGE": payload["imageId"] = $"img-{i}"; break;
            default: payload["customerId"] = i; break;
        }

        var json = JsonSerializer.Serialize(new { jobType = _type, payload });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private async Task<string> SignInAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var user = options.Get("user", "operator");
        using var response = await http.PostAsJsonAsync("/api/v1/auth/token",
            new { username = user, password = options.Get("password", user) }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return token.GetProperty("accessToken").GetString()!;
    }
}
