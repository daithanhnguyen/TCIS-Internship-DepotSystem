using Microsoft.EntityFrameworkCore;
using TechSpherex.CleanArchitecture.Application.Abstractions.Data;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Entities;
using TechSpherex.CleanArchitecture.Domain.Enums;
namespace TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.Shared;

// Queries shared by gate-in, the container catalog and the assignment commands
public static class OperatingAssignmentQueries
{
    // Confirmed and covering the date: ValidFrom <= date and (no end or date <= ValidTo), translated to SQL
    public static IQueryable<OperatingAssignment> ActiveOn(this IQueryable<OperatingAssignment> assignments, DateOnly date) =>
        assignments.Where(a =>
            a.Status == OperatingAssignmentStatus.Confirmed &&
            a.ValidFrom <= date &&
            (a.ValidTo == null || a.ValidTo >= date));

    /*
        The confirmed assignment of a container on a Vietnam date, with its operator.
        Confirmed periods never overlap (checked on confirm), so there is at most one.
    */
    public static Task<OperatingAssignment?> FindActiveAsync(
        this IAppDbContext dbContext, Guid containerId, DateOnly date, CancellationToken cancellationToken) =>
        dbContext.OperatingAssignments
            .Include(a => a.Operator)
            .Where(a => a.ContainerId == containerId)
            .ActiveOn(date)
            .OrderByDescending(a => a.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);

    /*
        Feedback 2: at most one confirmed operator per container on any day, otherwise gate-in could not
        tell which operator applies ("mâu thuẫn dữ liệu"). Checked before an assignment becomes confirmed.
    */
    public static async Task<Result> CheckNoConfirmedOverlapAsync(
        this IAppDbContext dbContext, OperatingAssignment assignment, CancellationToken cancellationToken)
    {
        var to = assignment.ValidTo ?? DateOnly.MaxValue;
        var clash = await dbContext.OperatingAssignments
            .Include(a => a.Operator)
            .Where(a =>
                a.Id != assignment.Id &&
                a.ContainerId == assignment.ContainerId &&
                a.Status == OperatingAssignmentStatus.Confirmed &&
                a.ValidFrom <= to &&
                (a.ValidTo == null || a.ValidTo >= assignment.ValidFrom))
            .FirstOrDefaultAsync(cancellationToken);

        return clash is null
            ? Result.Success()
            : Result.Failure(Error.Conflict("OperatingAssignment.Overlap",
                $"The period overlaps the confirmed assignment to {clash.Operator.Code} from {clash.ValidFrom:dd/MM/yyyy}" +
                (clash.ValidTo is { } end ? $" to {end:dd/MM/yyyy}" : " (open-ended)") + ". End that one first."));
    }

    // Projection used by the list and the "active" lookup
    public static IQueryable<OperatingAssignmentResponse> ToResponse(
        this IQueryable<OperatingAssignment> assignments, IAppDbContext dbContext) =>
        assignments.Select(a => new OperatingAssignmentResponse(
            a.Id, a.ContainerId, a.Container.ContainerNumber,
            a.OperatorId, a.Operator.Code, a.Operator.Name,
            a.ValidFrom, a.ValidTo, a.Status, a.Reference, a.Note,
            a.CreatedAt, a.ConfirmedAt, a.EndedAt, a.CancelledAt, a.CancelReason,
            dbContext.ContainerVisits.Count(v => v.OperatingAssignmentId == a.Id)));

    public static Error NotFound(Guid id) =>
        Error.NotFound("OperatingAssignment.NotFound", $"Operating assignment with ID '{id}' was not found.");
}
