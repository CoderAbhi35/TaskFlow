using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;

namespace Reeve.Application.Jobs;

/// <summary>
/// A stable hash of the fields that define a create-job request. Formatting differences in the JSON
/// (whitespace) do not change it; any change to the job's content does.
/// </summary>
public static class RequestFingerprint
{
    public static string Compute(CreateJobRequest request)
    {
        var canonical = request with
        {
            JobType = request.JobType?.Trim(),
            Priority = request.Priority ?? JobPriority.Normal,
            ScheduledAt = request.ScheduledAt?.ToUniversalTime(),
        };

        // Compact serialisation normalises the payload's whitespace.
        var json = JsonSerializer.Serialize(canonical, ReeveJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
