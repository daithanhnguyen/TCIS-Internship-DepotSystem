using Microsoft.EntityFrameworkCore;
using TechSpherex.CleanArchitecture.Application.Abstractions.Data;
using TechSpherex.CleanArchitecture.Application.Abstractions.Messaging;
using TechSpherex.CleanArchitecture.Application.Features.Containers.Shared;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.Shared;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Entities;
using TechSpherex.CleanArchitecture.Domain.Enums;
using TechSpherex.CleanArchitecture.Domain.Services;
namespace TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GetGateOutCandidates;

/*
    Order of the rows: best number match first (when searching), then
      0 in the yard and free of repair, 1 in the yard but held by repair or damage, 2 not in the yard,
      then by number.
    The rank is computed in SQL, so paging keeps the containers that can leave on page 1.
    "Container above" needs the yard rules and is worked out for the rows of the page only.
    A cancelled or expired order gives 409 (the dropdown of orders only offers usable ones).
*/
public sealed class GetGateOutCandidatesQueryHandler(IAppDbContext dbContext, TimeProvider timeProvider)
    : IQueryHandler<GetGateOutCandidatesQuery, Result<PagedResult<GateOutCandidate>>>
{
    public async Task<Result<PagedResult<GateOutCandidate>>> HandleAsync(
        GetGateOutCandidatesQuery query, CancellationToken cancellationToken = default)
    {
        var order = await dbContext.DeliveryOrders.AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == query.DeliveryOrderId, cancellationToken);
        if (order is null)
            return Result.Failure<PagedResult<GateOutCandidate>>(Error.NotFound("DeliveryOrder.NotFound",
                $"Delivery order with ID '{query.DeliveryOrderId}' was not found."));

        var today = DepotTime.DateOf(timeProvider.GetUtcNow());
        if (order.IsCancelled)
            return Result.Failure<PagedResult<GateOutCandidate>>(Error.Conflict("DeliveryOrder.Cancelled",
                $"Delivery order {order.OrderNumber} has been cancelled."));
        if (order.IsExpired(today))
            return Result.Failure<PagedResult<GateOutCandidate>>(Error.Conflict("DeliveryOrder.Expired",
                $"Delivery order {order.OrderNumber} expired on {order.ExpiryDate:dd/MM/yyyy}."));

        // Types still allowed for each size (a line without size takes both sizes)
        var open = order.Lines.Where(l => l.RemainingQuantity > 0).ToList();
        var types20 = open.Where(l => l.Size != ContainerSize.Ft40).Select(l => l.Category).Distinct().ToList();
        var types40 = open.Where(l => l.Size != ContainerSize.Ft20).Select(l => l.Category).Distinct().ToList();

        /*
            The operator's containers: on an open visit of this operator, or out of the yard and operated
            by it today. A container in the yard on another operator's visit is left out: Q5 keeps the
            operator of the open visit, so it cannot leave under this order.
        */
        var operatorId = order.LineOperatorId;
        var inYardIds = dbContext.ContainerVisits
            .Where(v => v.GateOutAt == null && v.LineOperatorId == operatorId)
            .Select(v => v.ContainerId);
        var anyOpenVisitIds = dbContext.ContainerVisits.Where(v => v.GateOutAt == null).Select(v => v.ContainerId);
        var operatedToday = dbContext.OperatingAssignments.ActiveOn(today)
            .Where(a => a.OperatorId == operatorId)
            .Select(a => a.ContainerId);

        // Feedback 26/10: only the containers the order still needs (type + size of a line with quantity left)
        var containers = dbContext.Containers.AsNoTracking()
            .Where(c => inYardIds.Contains(c.Id) || (operatedToday.Contains(c.Id) && !anyOpenVisitIds.Contains(c.Id)))
            .Where(c => (c.Size == ContainerSize.Ft20 && types20.Contains(c.Category)) ||
                        (c.Size == ContainerSize.Ft40 && types40.Contains(c.Category)));

        // Numbers are stored normalized (trim, upper case); "" = no search
        var search = string.IsNullOrWhiteSpace(query.Search) ? "" : ContainerNumberRules.Normalize(query.Search);
        if (search.Length > 0)
            containers = containers.Where(c => c.ContainerNumber.Contains(search));

        var totalCount = await containers.CountAsync(cancellationToken);

        // Rank as described above, translated to one CASE in SQL
        var ranked = containers.Select(c => new
        {
            Container = c,
            Rank = !inYardIds.Contains(c.Id) ? 2
                : dbContext.RepairCases.Any(rc => rc.ContainerId == c.Id && rc.Status != RepairCaseStatus.Passed) ||
                  c.Condition == ContainerCondition.Damaged ? 1
                : 0
        });

        var ordered = search.Length == 0
            ? ranked.OrderBy(r => r.Rank).ThenBy(r => r.Container.ContainerNumber)
            : ranked.OrderBy(r => r.Container.ContainerNumber == search ? 0 : r.Container.ContainerNumber.StartsWith(search) ? 1 : 2)
                .ThenBy(r => r.Rank)
                .ThenBy(r => r.Container.ContainerNumber);

        var page = await ordered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(r => r.Container)
            .ToListAsync(cancellationToken);

        var items = await ToCandidatesAsync(page, operatorId, cancellationToken);
        return Result.Success(new PagedResult<GateOutCandidate>(items, totalCount, query.Page, query.PageSize));
    }

    // Open visit, open case and situation of the page's containers (a few queries for the page, not per row)
    private async Task<List<GateOutCandidate>> ToCandidatesAsync(
        List<Container> page, Guid operatorId, CancellationToken cancellationToken)
    {
        var ids = page.Select(c => c.Id).ToList();

        var visits = await dbContext.ContainerVisits.AsNoTracking()
            .Where(v => ids.Contains(v.ContainerId) && v.GateOutAt == null && v.LineOperatorId == operatorId)
            .Select(v => new { v.Id, v.ContainerId, v.BlockId, BlockCode = v.Block.Code, v.Bay, v.Row, v.Tier, v.GateInAt })
            .ToListAsync(cancellationToken);
        var visitByContainer = visits.GroupBy(v => v.ContainerId).ToDictionary(g => g.Key, g => g.MaxBy(v => v.GateInAt)!);

        // At most one open case per container (unique index)
        var cases = await dbContext.RepairCases.AsNoTracking()
            .Where(rc => ids.Contains(rc.ContainerId) && rc.Status != RepairCaseStatus.Passed)
            .Select(rc => new { rc.Id, rc.ContainerId, rc.Status })
            .ToListAsync(cancellationToken);
        var caseByContainer = cases.GroupBy(rc => rc.ContainerId).ToDictionary(g => g.Key, g => g.First());

        // Same "where is it" line as the gate-in dropdown
        var situations = await dbContext.LoadSituationsAsync(ids, cancellationToken);

        var items = new List<GateOutCandidate>(page.Count);
        foreach (var container in page)
        {
            var visit = visitByContainer.GetValueOrDefault(container.Id);
            var repairCase = caseByContainer.GetValueOrDefault(container.Id);
            // "Container above" is only looked up when nothing else blocks the release
            string? above = null;
            string? reason = visit is null ? "NotInYard"
                : repairCase is not null ? "RepairCase"
                : container.Condition == ContainerCondition.Damaged ? "Damaged"
                : null;
            if (reason is null && visit is not null)
            {
                above = await FindContainerAboveAsync(visit.BlockId, visit.Bay, visit.Row, visit.Tier, container.Size, cancellationToken);
                if (above is not null) reason = "ContainerAbove";
            }

            items.Add(new GateOutCandidate(
                visit?.Id, container.Id, container.ContainerNumber, container.IsoCode, container.Size, container.Category,
                visit?.BlockCode, visit?.Bay, visit?.Row, visit?.Tier, container.Condition, visit?.GateInAt,
                reason, repairCase?.Id, repairCase?.Status, above, situations.GetValueOrDefault(container.Id)));
        }

        return items;
    }

    // Number of the container resting on this one (same rule as YardPlacement.CheckNothingAboveAsync), or null
    private async Task<string?> FindContainerAboveAsync(
        Guid blockId, int? bay, int? row, int? tier, ContainerSize size, CancellationToken cancellationToken)
    {
        if (bay is null || row is null || tier is null)
            return null; // virtual block: no stacking

        var upper = await dbContext.ContainerVisits.AsNoTracking()
            .Where(v => v.BlockId == blockId && v.GateOutAt == null && v.Row == row && v.Tier == tier + 1)
            .Select(v => new { Bay = v.Bay!.Value, Tier = v.Tier!.Value, v.Container.Size, v.Container.ContainerNumber })
            .ToListAsync(cancellationToken);

        return upper.FirstOrDefault(u =>
            YardSlotRules.Overlaps(bay.Value, tier.Value + 1, size, [new YardSlotRules.PlacedContainer(u.Bay, u.Tier, u.Size)]))
            ?.ContainerNumber;
    }
}
