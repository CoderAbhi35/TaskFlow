using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Domain.Schedules;

namespace Reeve.Infrastructure.Persistence;

internal sealed class ScheduleRepository(ReeveDbContext db) : IScheduleRepository
{
    public void Add(Schedule schedule) => db.Schedules.Add(schedule);

    public void Remove(Schedule schedule) => db.Schedules.Remove(schedule);

    public Task<Schedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Schedules.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Schedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken);
}
