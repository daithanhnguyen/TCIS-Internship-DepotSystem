using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Enums;

namespace TechSpherex.CleanArchitecture.Domain.Entities;

/*
    Operating period of a container (feedback 2: "giai đoạn khai thác"): which line operator
    operates the container from ValidFrom to ValidTo (both Vietnam dates, both included; no ValidTo = open-ended).
    Example: HMM owns HMMU..., CMA rents it, so the assignment says CMA - the owner stays HMM on the container.

    Gate-in uses the Confirmed assignment that covers the gate-in date; the visit keeps a reference to it
    (căn cứ phân công). Changing or ending an assignment later never rewrites a visit (Q5).
    The rows themselves, with their dates and status times, are the update history.
*/
public sealed class OperatingAssignment : AuditableEntity
{
    public Guid ContainerId { get; private set; }
    public Container Container { get; private set; } = default!;
    public Guid OperatorId { get; private set; }
    public LineOperator Operator { get; private set; } = default!;

    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public OperatingAssignmentStatus Status { get; private set; }

    // Contract / instruction number the relation is based on (no full lease contract module yet)
    public string? Reference { get; private set; }
    public string? Note { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }

    private OperatingAssignment() { }

    public static Result<OperatingAssignment> Create(
        Container container, LineOperator lineOperator, DateOnly validFrom, DateOnly? validTo,
        string? reference, string? note)
    {
        if (!lineOperator.IsOperator)
            return Result.Failure<OperatingAssignment>(Error.Conflict("Partner.NotOperator",
                $"Partner {lineOperator.Code} is not marked as a line operator."));

        if (validTo < validFrom)
            return Result.Failure<OperatingAssignment>(Error.Validation("OperatingAssignment.InvalidPeriod",
                "The end date cannot be before the start date."));

        return Result.Success(new OperatingAssignment
        {
            ContainerId = container.Id,
            OperatorId = lineOperator.Id,
            ValidFrom = validFrom,
            ValidTo = validTo,
            Status = OperatingAssignmentStatus.Pending,
            Reference = Clean(reference),
            Note = Clean(note)
        });
    }

    // Verified against the contract / instruction: from now on gate-in may use it
    public Result Confirm(DateTimeOffset at)
    {
        if (Status != OperatingAssignmentStatus.Pending)
            return Result.Failure(Error.Conflict("OperatingAssignment.NotPending",
                "Only a pending assignment can be confirmed."));

        Status = OperatingAssignmentStatus.Confirmed;
        ConfirmedAt = at;
        return Result.Success();
    }

    // Ends (or shortens) a confirmed assignment; the dates already used by visits stay covered by the handler check
    public Result End(DateOnly validTo, DateTimeOffset at)
    {
        if (Status != OperatingAssignmentStatus.Confirmed)
            return Result.Failure(Error.Conflict("OperatingAssignment.NotConfirmed",
                "Only a confirmed assignment can be ended."));

        if (validTo < ValidFrom)
            return Result.Failure(Error.Validation("OperatingAssignment.InvalidPeriod",
                "The end date cannot be before the start date."));

        ValidTo = validTo;
        EndedAt = at;
        return Result.Success();
    }

    // Recorded by mistake; the handler refuses when a visit already used it (that would rewrite history)
    public Result Cancel(string reason, DateTimeOffset at)
    {
        if (Status == OperatingAssignmentStatus.Cancelled)
            return Result.Failure(Error.Conflict("OperatingAssignment.AlreadyCancelled",
                "The assignment is already cancelled."));

        Status = OperatingAssignmentStatus.Cancelled;
        CancelledAt = at;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
