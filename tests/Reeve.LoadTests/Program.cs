using System.Text.Json;
using Reeve.LoadTests;

// Load and failure test driver. Jobs go in through the public HTTP entry point (the dashboard's
// proxy); everything after submission is measured from PostgreSQL, the system of record, and from
// Kafka's offsets. See README.md for the scenarios.
//
//   submit   submit --jobs N at --concurrency C (or a fixed --rate), then wait and analyse the run
//   measure  wait for an earlier run (--run-id) to finish and analyse it

var options = Options.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var runId = options.Get("run-id", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
var result = new RunResult { RunId = runId, Name = options.Get("name", runId), Command = options.Command, StartedAt = DateTimeOffset.UtcNow };
result.Settings = options.All;

await using var sampler = new Sampler(options);
sampler.Start();

if (options.Command == "submit")
{
    var submitter = new Submitter(options, runId);
    result.Submission = await submitter.RunAsync(cts.Token);
    Console.WriteLine(result.Submission.Summary());
}
else if (options.Command != "measure")
{
    Console.Error.WriteLine("Usage: submit|measure [--option value]...  (see README.md)");
    return 2;
}

if (!options.Flag("no-wait"))
{
    var analysis = new RunAnalysis(options.Get("db", Options.DefaultDb));
    var timeout = TimeSpan.FromSeconds(options.GetInt("timeout-seconds", 1800));
    var finished = await analysis.WaitForRunAsync(runId, timeout, cts.Token);
    result.Execution = await analysis.AnalyseAsync(runId, cts.Token);
    result.Execution.TimedOut = !finished;
    Console.WriteLine(result.Execution.Summary());
}

result.FinishedAt = DateTimeOffset.UtcNow;
result.Samples = await sampler.StopAsync();
Console.WriteLine(result.Samples.Summary());

if (options.Get("out", "") is { Length: > 0 } path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, RunResult.Json));
    Console.WriteLine($"Result written to {path}");
}

return 0;
