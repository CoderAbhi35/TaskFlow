using FluentAssertions;
using FluentValidation;
using Reeve.Application.Jobs;
using Reeve.Contracts.Jobs;
using DomainModel = Reeve.Domain.Jobs;

namespace Reeve.UnitTests.Application;

public class JobSearchCriteriaTests
{
    [Fact]
    public void Empty_request_uses_defaults()
    {
        var criteria = JobSearchCriteria.From(new SearchJobsRequest());

        criteria.Statuses.Should().BeEmpty();
        criteria.Limit.Should().Be(JobSearchCriteria.DefaultLimit);
        criteria.Before.Should().BeNull();
    }

    [Theory]
    [InlineData("DEAD_LETTERED")]
    [InlineData("dead_lettered")]
    [InlineData("DeadLettered")]
    public void Accepts_wire_spelling_of_statuses(string value)
    {
        JobSearchCriteria.From(new SearchJobsRequest { Status = [value] })
            .Statuses.Should().Equal(DomainModel.JobStatus.DeadLettered);
    }

    [Fact]
    public void Removes_duplicate_statuses()
    {
        JobSearchCriteria.From(new SearchJobsRequest { Status = ["FAILED", "failed"] })
            .Statuses.Should().Equal(DomainModel.JobStatus.Failed);
    }

    [Fact]
    public void Cursor_round_trips()
    {
        var id = Guid.CreateVersion7();

        JobSearchCriteria.From(new SearchJobsRequest { Cursor = JobSearchCriteria.EncodeCursor(id) })
            .Before.Should().Be(id);
    }

    [Fact]
    public void Reports_every_invalid_filter_at_once()
    {
        var act = () => JobSearchCriteria.From(new SearchJobsRequest
        {
            Status = ["1"],
            Priority = "EXTREME",
            Limit = 0,
            Cursor = "nope",
        });

        act.Should().Throw<ValidationException>()
            .Which.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo("status", "priority", "limit", "cursor");
    }
}
