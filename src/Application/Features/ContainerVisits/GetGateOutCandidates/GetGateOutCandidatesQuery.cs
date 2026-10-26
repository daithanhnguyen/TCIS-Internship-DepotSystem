using TechSpherex.CleanArchitecture.Application.Abstractions.Messaging;
using TechSpherex.CleanArchitecture.Application.Features.Containers.Shared;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Enums;
namespace TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GetGateOutCandidates;

/*
    Feedback 6 + 26/10: the container dropdown of gate-out, after the delivery order was chosen.
    Lists the containers of the order: the order's line operator and a type + size a line still needs.
    - in the yard on a visit of that operator (taken from the confirmed assignment at gate-in, never from the prefix);
    - not in the yard but operated by it today (confirmed assignment): at an outside facility, released, never came in.
    Each row says whether it can leave now and, if not, why, so the clerk sees "not allowed yet"
    and can open the screen that resolves it, instead of "not found".
    Search: part of the container number (exact, then prefix, then the rest), as in every search box.
*/
public sealed record GetGateOutCandidatesQuery(Guid DeliveryOrderId, string? Search = null, int Page = 1, int PageSize = 20)
    : IQuery<Result<PagedResult<GateOutCandidate>>>;

/*
    Why a container of the operator cannot be released for this order now (null = it can), first match wins:
      NotInYard      - no open visit (at an outside facility, already released, or never came in); see Situation
      RepairCase     - an open inspection / repair case (RepairCaseId, RepairStatus); clicking opens the case
      Damaged        - marked damaged without an open case (older data); the repair page opens one at start-up
      ContainerAbove - another container rests on it (AboveContainerNumber); not a repair matter
    VisitId, BlockCode and GateInAt are null when the container is not in the yard.
*/
public sealed record GateOutCandidate(
    Guid? VisitId,
    Guid ContainerId,
    string ContainerNumber,
    string IsoCode,
    ContainerSize Size,
    ContainerCategory Category,
    string? BlockCode,
    int? Bay,
    int? Row,
    int? Tier,
    ContainerCondition Condition,
    DateTimeOffset? GateInAt,
    string? BlockReason,
    Guid? RepairCaseId,
    RepairCaseStatus? RepairStatus,
    string? AboveContainerNumber,
    ContainerSituation? Situation)
{
    public bool Eligible => BlockReason is null;
}
