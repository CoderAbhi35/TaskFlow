using FluentAssertions;
using Reeve.Application.Common;
using Api = Reeve.Contracts;
using DomainModel = Reeve.Domain;

namespace Reeve.UnitTests.Application;

/// <summary>Adding a member to a domain enum without its API counterpart (or vice versa) fails here.</summary>
public class EnumMapperTests
{
    [Fact] public void JobStatus_maps_both_ways() => AssertRoundTrip<DomainModel.Jobs.JobStatus, Api.Jobs.JobStatus>();
    [Fact] public void JobPriority_maps_both_ways() => AssertRoundTrip<DomainModel.Jobs.JobPriority, Api.Jobs.JobPriority>();
    [Fact] public void JobAttemptStatus_maps_both_ways() => AssertRoundTrip<DomainModel.Jobs.JobAttemptStatus, Api.Jobs.JobAttemptStatus>();
    [Fact] public void WorkerStatus_maps_both_ways() => AssertRoundTrip<DomainModel.Workers.WorkerStatus, Api.Workers.WorkerStatus>();

    private static void AssertRoundTrip<TDomain, TApi>()
        where TDomain : struct, Enum
        where TApi : struct, Enum
    {
        Enum.GetNames<TDomain>().Should().BeEquivalentTo(Enum.GetNames<TApi>());
        foreach (var value in Enum.GetValues<TDomain>())
            EnumMapper.Map<TApi, TDomain>(EnumMapper.Map<TDomain, TApi>(value)).Should().Be(value);
    }
}
