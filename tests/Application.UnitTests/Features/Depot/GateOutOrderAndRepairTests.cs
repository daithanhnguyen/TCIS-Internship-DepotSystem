using FluentAssertions;
using TechSpherex.CleanArchitecture.Application.Features.Containers.GetAll;
using TechSpherex.CleanArchitecture.Application.Features.Containers.Shared;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GateIn;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GateOut;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GateOutForRepair;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GetDaily;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GetGateOutCandidates;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.History;
using TechSpherex.CleanArchitecture.Application.Features.DeliveryOrders.Cancel;
using TechSpherex.CleanArchitecture.Application.Features.DeliveryOrders.GetAll;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Act;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.External;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Get;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Photos;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Entities;
using TechSpherex.CleanArchitecture.Domain.Enums;
using TechSpherex.CleanArchitecture.Infrastructure.Persistence;
namespace TechSpherex.CleanArchitecture.Application.UnitTests.Features.Depot;

// Feedback 6 (gate-out by order, lines by type + size, statuses, cancel) and feedback 4 (outside repair)
public sealed class GateOutOrderAndRepairTests
{
    // 21/10/2026 09:00 in Vietnam
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateOnly Today = new(2026, 10, 21);

    private sealed record Seed(LineOperator Msc, LineOperator Cma, Customer Customer, Block Yard);

    private static async Task<Seed> SeedAsync(AppDbContext dbContext)
    {
        var seed = new Seed(DepotTestData.Partner("MSC"), DepotTestData.Partner("CMA"),
            new Customer("0301234567", "Customer A"), Block.CreatePhysical("A", maxBays: 20, maxRows: 4, maxTiers: 3));
        dbContext.AddRange(seed.Msc, seed.Cma, seed.Customer, seed.Yard);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return seed;
    }

    private static async Task<Container> AddContainerAsync(
        AppDbContext dbContext, string number, ContainerSize size = ContainerSize.Ft20, ContainerCategory category = ContainerCategory.Dry)
    {
        var iso = (size, category) switch
        {
            (ContainerSize.Ft20, ContainerCategory.Reefer) => "22R1",
            (ContainerSize.Ft40, ContainerCategory.Dry) => "42G1",
            _ => "22G1"
        };
        var container = new Container(number, iso, size, category, 30480, 2200, null, "MSC");
        dbContext.Containers.Add(container);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return container;
    }

    private static async Task<DeliveryOrder> AddOrderAsync(
        AppDbContext dbContext, Seed seed, string number = "DO-001", DateOnly? expiry = null,
        params (ContainerCategory Category, ContainerSize Size, int Quantity)[] lines)
    {
        var order = new DeliveryOrder(number, seed.Msc.Id, seed.Customer.Id, expiry ?? Today.AddDays(10), "MSC ANNA 042W");
        foreach (var line in lines.Length > 0 ? lines : [(ContainerCategory.Dry, ContainerSize.Ft20, 2)])
            order.AddLine(line.Category, line.Quantity, line.Size).IsSuccess.Should().BeTrue();
        dbContext.DeliveryOrders.Add(order);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return order;
    }

    private static async Task<GateInResponse> GateInAsync(
        AppDbContext dbContext, Seed seed, Container container, int bay, int tier = 1,
        ContainerCondition condition = ContainerCondition.Normal, LineOperator? lineOperator = null)
    {
        await DepotTestData.AssignAsync(dbContext, container, lineOperator ?? seed.Msc);
        var result = await new GateInCommandHandler(dbContext, new FixedTimeProvider(Now), new FakeCurrentUser()).HandleAsync(
            new GateInCommand(container.Id, (lineOperator ?? seed.Msc).Id, seed.Yard.Id, bay, 1, tier,
                condition, condition == ContainerCondition.Damaged ? "Thủng vách" : null, "51C-11111"),
            TestContext.Current.CancellationToken);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        return result.Value!;
    }

    private static Task<Result<GateOutResponse>> GateOutAsync(AppDbContext dbContext, Container container, DeliveryOrder order) =>
        new GateOutCommandHandler(dbContext, new FixedTimeProvider(Now)).HandleAsync(
            new GateOutCommand(container.Id, order.Id, "51C-99999"), TestContext.Current.CancellationToken);

    private static Task<Result> ActAsync(AppDbContext dbContext, Guid caseId, RepairAction action) =>
        new ApplyRepairActionCommandHandler(dbContext, new FixedTimeProvider(Now), new FakeCurrentUser())
            .HandleAsync(new ApplyRepairActionCommand(caseId, action, null), TestContext.Current.CancellationToken);

    private static Task<Result<Guid>> RequestExternalAsync(AppDbContext dbContext, Guid caseId) =>
        new RequestExternalRepairCommandHandler(dbContext, new FixedTimeProvider(Now), new FakeCurrentUser()).HandleAsync(
            new RequestExternalRepairCommand(caseId, "Xưởng Cát Lái", "Thay vách, cần hàn", "CMA-APP-0042"),
            TestContext.Current.CancellationToken);

    private static Task<Result<GateOutForRepairResponse>> GateOutForRepairAsync(AppDbContext dbContext, Guid transferId) =>
        new GateOutForRepairCommandHandler(dbContext, new FixedTimeProvider(Now), new FakeCurrentUser()).HandleAsync(
            new GateOutForRepairCommand(transferId, "51C-77777"), TestContext.Current.CancellationToken);

    // ---------- delivery orders: lines by type + size (Q1), cancel (Q7), status ----------

    [Fact]
    public async Task Line_Should_Match_Type_And_Size()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var order = await AddOrderAsync(dbContext, seed, lines: (ContainerCategory.Dry, ContainerSize.Ft20, 1));
        var dry40 = await AddContainerAsync(dbContext, "MSCU1234566", ContainerSize.Ft40);
        var dry20 = await AddContainerAsync(dbContext, "MSCU5551234");
        await GateInAsync(dbContext, seed, dry40, bay: 2);
        await GateInAsync(dbContext, seed, dry20, bay: 5);

        var wrongSize = await GateOutAsync(dbContext, dry40, order);
        var rightSize = await GateOutAsync(dbContext, dry20, order);

        wrongSize.Error!.Code.Should().Be("DeliveryOrder.TypeNotAllowed");
        rightSize.Value!.Size.Should().Be(ContainerSize.Ft20);
        rightSize.Value.RemainingQuantity.Should().Be(0);
        dbContext.ContainerVisits.Single(v => v.ContainerId == dry20.Id).ExitPurpose.Should().Be(GateOutPurpose.EmptyRelease);
    }

    [Fact]
    public void Order_Should_Refuse_Overlapping_Lines()
    {
        var order = new DeliveryOrder("DO-1", Guid.NewGuid(), Guid.NewGuid(), Today, "V1");

        order.AddLine(ContainerCategory.Dry, 2, ContainerSize.Ft20).IsSuccess.Should().BeTrue();
        order.AddLine(ContainerCategory.Dry, 1, ContainerSize.Ft40).IsSuccess.Should().BeTrue();
        order.AddLine(ContainerCategory.Dry, 1, ContainerSize.Ft20).Error!.Code.Should().Be("DeliveryOrder.DuplicateLine");
        // A line without size takes both sizes, so it cannot sit next to a sized line of the same type
        order.AddLine(ContainerCategory.Dry, 1).Error!.Code.Should().Be("DeliveryOrder.DuplicateLine");
    }

    [Fact]
    public async Task Cancel_Should_Need_No_Release_And_Block_Later_Releases()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var unused = await AddOrderAsync(dbContext, seed, "DO-001");
        var used = await AddOrderAsync(dbContext, seed, "DO-002");
        var dry = await AddContainerAsync(dbContext, "MSCU1234566");
        var dry2 = await AddContainerAsync(dbContext, "MSCU5551234");
        await GateInAsync(dbContext, seed, dry, bay: 1);
        await GateInAsync(dbContext, seed, dry2, bay: 3);
        await GateOutAsync(dbContext, dry, used);
        var cancel = new CancelDeliveryOrderCommandHandler(dbContext, new FixedTimeProvider(Now));

        var cancelled = await cancel.HandleAsync(new CancelDeliveryOrderCommand(unused.Id, "Khách huỷ booking"), TestContext.Current.CancellationToken);
        var again = await cancel.HandleAsync(new CancelDeliveryOrderCommand(unused.Id, "Lần 2"), TestContext.Current.CancellationToken);
        var withRelease = await cancel.HandleAsync(new CancelDeliveryOrderCommand(used.Id, "Muộn"), TestContext.Current.CancellationToken);
        var release = await GateOutAsync(dbContext, dry2, unused);

        cancelled.IsSuccess.Should().BeTrue();
        unused.CancelReason.Should().Be("Khách huỷ booking");
        again.Error!.Code.Should().Be("DeliveryOrder.AlreadyCancelled");
        withRelease.Error!.Code.Should().Be("DeliveryOrder.HasReleases");
        release.Error!.Code.Should().Be("DeliveryOrder.Cancelled");
        new CancelDeliveryOrderValidator().Validate(new CancelDeliveryOrderCommand(unused.Id, " ")).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task List_Should_Show_Status_And_Usable_Orders_Only_For_Gate_Out()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        await AddOrderAsync(dbContext, seed, "DO-NEW");
        var partly = await AddOrderAsync(dbContext, seed, "DO-PART");
        var full = await AddOrderAsync(dbContext, seed, "DO-FULL", lines: (ContainerCategory.Dry, ContainerSize.Ft20, 1));
        await AddOrderAsync(dbContext, seed, "DO-OLD", expiry: Today.AddDays(-1));
        var cancelled = await AddOrderAsync(dbContext, seed, "DO-CXL");
        cancelled.Cancel("Huỷ", Now).IsSuccess.Should().BeTrue();
        var a = await AddContainerAsync(dbContext, "MSCU1234566");
        var b = await AddContainerAsync(dbContext, "MSCU5551234");
        await GateInAsync(dbContext, seed, a, bay: 1);
        await GateInAsync(dbContext, seed, b, bay: 3);
        await GateOutAsync(dbContext, a, partly);
        await GateOutAsync(dbContext, b, full);
        var list = new GetAllDeliveryOrdersQueryHandler(dbContext, new FixedTimeProvider(Now));

        var all = await list.HandleAsync(new GetAllDeliveryOrdersQuery(PageSize: 50), TestContext.Current.CancellationToken);
        var usable = await list.HandleAsync(new GetAllDeliveryOrdersQuery(Usable: true), TestContext.Current.CancellationToken);
        var inProgress = await list.HandleAsync(new GetAllDeliveryOrdersQuery(Status: DeliveryOrderStatus.InProgress), TestContext.Current.CancellationToken);
        var search = await list.HandleAsync(new GetAllDeliveryOrdersQuery(Search: "do-part"), TestContext.Current.CancellationToken);

        all.Value!.Items.ToDictionary(o => o.OrderNumber, o => o.Status).Should().BeEquivalentTo(new Dictionary<string, DeliveryOrderStatus>
        {
            ["DO-NEW"] = DeliveryOrderStatus.NotStarted,
            ["DO-PART"] = DeliveryOrderStatus.InProgress,
            ["DO-FULL"] = DeliveryOrderStatus.Completed,
            ["DO-OLD"] = DeliveryOrderStatus.Expired,
            ["DO-CXL"] = DeliveryOrderStatus.Cancelled
        });
        usable.Value!.Items.Select(o => o.OrderNumber).Should().BeEquivalentTo("DO-NEW", "DO-PART");
        inProgress.Value!.Items.Should().ContainSingle(o => o.OrderNumber == "DO-PART");
        search.Value!.Items[0].OrderNumber.Should().Be("DO-PART");
    }

    // ---------- gate-out candidates (feedback 6) ----------

    [Fact]
    public async Task Candidates_Should_Match_Order_And_Explain_Blocked_Containers()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var order = await AddOrderAsync(dbContext, seed);
        var free = await AddContainerAsync(dbContext, "MSCU1234566");
        var damaged = await AddContainerAsync(dbContext, "MSCU5551234");
        var under = await AddContainerAsync(dbContext, "MSCU0000019");
        var above = await AddContainerAsync(dbContext, "MSCU0000024");
        var reefer = await AddContainerAsync(dbContext, "MSCU0000030", category: ContainerCategory.Reefer);
        var big = await AddContainerAsync(dbContext, "MSCU0000045", ContainerSize.Ft40);
        var otherLine = await AddContainerAsync(dbContext, "CMAU7654327");
        await GateInAsync(dbContext, seed, free, bay: 1);
        var damagedIn = await GateInAsync(dbContext, seed, damaged, bay: 3, condition: ContainerCondition.Damaged);
        await GateInAsync(dbContext, seed, under, bay: 5);
        await GateInAsync(dbContext, seed, above, bay: 5, tier: 2);
        await GateInAsync(dbContext, seed, reefer, bay: 7);
        await GateInAsync(dbContext, seed, big, bay: 12);
        await GateInAsync(dbContext, seed, otherLine, bay: 9, lineOperator: seed.Cma);
        var handler = new GetGateOutCandidatesQueryHandler(dbContext, new FixedTimeProvider(Now));

        var result = await handler.HandleAsync(new GetGateOutCandidatesQuery(order.Id), TestContext.Current.CancellationToken);
        var exact = await handler.HandleAsync(new GetGateOutCandidatesQuery(order.Id, "mscu0000024"), TestContext.Current.CancellationToken);

        // Reefer, 40ft and the CMA container do not match the order (MSC, Dry 20ft)
        result.Value!.Items.Select(c => c.ContainerNumber).Should()
            .BeEquivalentTo("MSCU1234566", "MSCU5551234", "MSCU0000019", "MSCU0000024");
        var byNumber = result.Value.Items.ToDictionary(c => c.ContainerNumber);
        byNumber["MSCU1234566"].Eligible.Should().BeTrue();
        byNumber["MSCU0000024"].Eligible.Should().BeTrue();
        byNumber["MSCU5551234"].BlockReason.Should().Be("RepairCase");
        byNumber["MSCU5551234"].RepairCaseId.Should().Be(damagedIn.RepairCaseId);
        byNumber["MSCU0000019"].BlockReason.Should().Be("ContainerAbove");
        byNumber["MSCU0000019"].AboveContainerNumber.Should().Be("MSCU0000024");
        // Containers that can leave come before the repair-blocked one
        result.Value.Items[^1].ContainerNumber.Should().Be("MSCU5551234");
        exact.Value!.Items.Should().ContainSingle(c => c.ContainerNumber == "MSCU0000024");
    }

    [Fact]
    public async Task Candidates_Should_Refuse_A_Cancelled_Or_Expired_Order()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var expired = await AddOrderAsync(dbContext, seed, "DO-OLD", Today.AddDays(-1));
        var cancelled = await AddOrderAsync(dbContext, seed, "DO-CXL");
        cancelled.Cancel("Huỷ", Now);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var handler = new GetGateOutCandidatesQueryHandler(dbContext, new FixedTimeProvider(Now));

        var old = await handler.HandleAsync(new GetGateOutCandidatesQuery(expired.Id), TestContext.Current.CancellationToken);
        var cxl = await handler.HandleAsync(new GetGateOutCandidatesQuery(cancelled.Id), TestContext.Current.CancellationToken);

        old.Error!.Code.Should().Be("DeliveryOrder.Expired");
        cxl.Error!.Code.Should().Be("DeliveryOrder.Cancelled");
    }

    // ---------- outside repair (feedback 4) ----------

    [Fact]
    public async Task External_Repair_Should_Leave_Without_Order_Stay_Open_And_Come_Back_On_A_New_Visit()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var order = await AddOrderAsync(dbContext, seed);
        var container = await AddContainerAsync(dbContext, "MSCU1234566");
        var first = await GateInAsync(dbContext, seed, container, bay: 1, condition: ContainerCondition.Damaged);
        var caseId = first.RepairCaseId!.Value;

        var tooEarly = await RequestExternalAsync(dbContext, caseId);
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        await RepairEstimateSteps.ApproveAsync(dbContext, caseId, new FixedTimeProvider(Now));
        var requested = await RequestExternalAsync(dbContext, caseId);
        var twice = await RequestExternalAsync(dbContext, caseId);
        var asEmpty = await GateOutAsync(dbContext, container, order);
        var left = await GateOutForRepairAsync(dbContext, requested.Value);

        tooEarly.Error!.Code.Should().Be("ExternalRepair.CaseNotApproved");
        twice.Error!.Code.Should().Be("ExternalRepair.AlreadyActive");
        asEmpty.Error!.Code.Should().Be("ContainerVisit.RepairCaseOpen");
        left.Value!.Facility.Should().Be("Xưởng Cát Lái");
        var firstVisit = dbContext.ContainerVisits.Single(v => v.Id == first.VisitId);
        firstVisit.ExitPurpose.Should().Be(GateOutPurpose.ExternalRepair);
        firstVisit.DeliveryOrderId.Should().BeNull();
        order.Lines.Single().ReleasedQuantity.Should().Be(0); // the order is not used
        container.Condition.Should().Be(ContainerCondition.Damaged); // never switched to Normal to get out
        dbContext.RepairCases.Single().IsOpen.Should().BeTrue();

        // Back from the facility: a new visit, linked to the transfer; still blocked until accepted
        var back = await GateInAsync(dbContext, seed, container, bay: 1);
        var transfer = dbContext.ExternalRepairTransfers.Single();

        back.ReturnedFromRepair.Should().BeTrue();
        back.RepairCaseId.Should().Be(caseId);
        back.VisitId.Should().NotBe(first.VisitId);
        transfer.Status.Should().Be(ExternalRepairStatus.Returned);
        transfer.ReturnVisitId.Should().Be(back.VisitId);
        container.Condition.Should().Be(ContainerCondition.Damaged);
        (await GateOutAsync(dbContext, container, order)).Error!.Code.Should().Be("ContainerVisit.RepairCaseOpen");

        await ActAsync(dbContext, caseId, RepairAction.StartRepair);
        await ActAsync(dbContext, caseId, RepairAction.CompleteRepair);
        (await ActAsync(dbContext, caseId, RepairAction.AcceptPassed)).IsSuccess.Should().BeTrue();
        (await GateOutAsync(dbContext, container, order)).IsSuccess.Should().BeTrue();
        dbContext.RepairCaseEvents.Select(e => e.Kind).Should().Contain(
            [RepairEventKind.ExternalRequested, RepairEventKind.SentForExternalRepair, RepairEventKind.ReturnedFromExternalRepair]);
    }

    [Fact]
    public async Task Case_Should_Not_Pass_While_A_Transfer_Is_Active_And_Cancel_Should_Free_It()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var container = await AddContainerAsync(dbContext, "MSCU1234566");
        var first = await GateInAsync(dbContext, seed, container, bay: 1, condition: ContainerCondition.Damaged);
        var caseId = first.RepairCaseId!.Value;
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        await RepairEstimateSteps.ApproveAsync(dbContext, caseId, new FixedTimeProvider(Now));
        await ActAsync(dbContext, caseId, RepairAction.StartRepair);
        var requested = await RequestExternalAsync(dbContext, caseId);
        await ActAsync(dbContext, caseId, RepairAction.CompleteRepair);

        var blocked = await ActAsync(dbContext, caseId, RepairAction.AcceptPassed);
        var detail = await new GetRepairCaseQueryHandler(dbContext)
            .HandleAsync(new GetRepairCaseQuery(caseId), TestContext.Current.CancellationToken);
        var cancelled = await new CancelExternalRepairCommandHandler(dbContext, new FixedTimeProvider(Now), new FakeCurrentUser())
            .HandleAsync(new CancelExternalRepairCommand(requested.Value, "Sửa được tại bãi"), TestContext.Current.CancellationToken);
        var leaveAfterCancel = await GateOutForRepairAsync(dbContext, requested.Value);
        var passed = await ActAsync(dbContext, caseId, RepairAction.AcceptPassed);

        blocked.Error!.Code.Should().Be("ExternalRepair.Active");
        detail.Value!.AllowedActions.Should().BeEquivalentTo([RepairAction.AcceptRejected]);
        detail.Value.CanRequestExternal.Should().BeFalse();
        detail.Value.Transfers.Should().ContainSingle(t => t.Status == ExternalRepairStatus.Requested && t.BlockCode == "A");
        cancelled.IsSuccess.Should().BeTrue();
        leaveAfterCancel.Error!.Code.Should().Be("ExternalRepair.NotRequested");
        passed.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Daily_Gate_Outs_Should_Show_The_Purpose_And_Facility()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var container = await AddContainerAsync(dbContext, "MSCU1234566");
        var first = await GateInAsync(dbContext, seed, container, bay: 1, condition: ContainerCondition.Damaged);
        await ActAsync(dbContext, first.RepairCaseId!.Value, RepairAction.InspectNeedsRepair);
        await RepairEstimateSteps.ApproveAsync(dbContext, first.RepairCaseId!.Value, new FixedTimeProvider(Now));
        var requested = await RequestExternalAsync(dbContext, first.RepairCaseId!.Value);
        await GateOutForRepairAsync(dbContext, requested.Value);

        var daily = await new GetDailyVisitsQueryHandler(dbContext, new FixedTimeProvider(Now))
            .HandleAsync(new GetDailyVisitsQuery(), TestContext.Current.CancellationToken);

        var row = daily.Value!.GateOuts.Single();
        row.ExitPurpose.Should().Be(GateOutPurpose.ExternalRepair);
        row.RepairFacility.Should().Be("Xưởng Cát Lái");
        row.DeliveryOrderNumber.Should().BeNull();
    }

    /*
        Feedback 23/10 (gate-in dropdown): the container list says where each container is.
        MSCU1234566 in the yard at A 01-1-1 with an open case, MSCU5551234 released as an empty,
        CMAU1234564 at the outside facility, CSQU3054383 never came in.
    */
    [Fact]
    public async Task Container_List_Should_Show_Where_Each_Container_Is()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var order = await AddOrderAsync(dbContext, seed);
        var damaged = await AddContainerAsync(dbContext, "MSCU1234566");
        var released = await AddContainerAsync(dbContext, "MSCU5551234");
        var outside = await AddContainerAsync(dbContext, "CMAU1234564");
        await AddContainerAsync(dbContext, "CSQU3054383");

        var damagedIn = await GateInAsync(dbContext, seed, damaged, bay: 1, condition: ContainerCondition.Damaged);
        await GateInAsync(dbContext, seed, released, bay: 3);
        (await GateOutAsync(dbContext, released, order)).IsSuccess.Should().BeTrue();
        var outsideIn = await GateInAsync(dbContext, seed, outside, bay: 5, condition: ContainerCondition.Damaged);
        var outsideCase = outsideIn.RepairCaseId!.Value;
        await ActAsync(dbContext, outsideCase, RepairAction.InspectNeedsRepair);
        await RepairEstimateSteps.ApproveAsync(dbContext, outsideCase, new FixedTimeProvider(Now));
        var requested = await RequestExternalAsync(dbContext, outsideCase);
        (await GateOutForRepairAsync(dbContext, requested.Value)).IsSuccess.Should().BeTrue();

        var page = await new GetAllContainersQueryHandler(dbContext, new FixedTimeProvider(Now))
            .HandleAsync(new GetAllContainersQuery(PageSize: 10), TestContext.Current.CancellationToken);
        var byNumber = page.Value!.Items.ToDictionary(c => c.ContainerNumber, c => c.Situation!);

        var inYard = byNumber["MSCU1234566"];
        inYard.InYard.Should().BeTrue();
        inYard.BlockCode.Should().Be("A");
        inYard.Bay.Should().Be(1);
        inYard.Row.Should().Be(1);
        inYard.Tier.Should().Be(1);
        inYard.OpenRepairCaseId.Should().Be(damagedIn.RepairCaseId);
        inYard.OpenRepairStatus.Should().Be("AwaitingInspection");

        var gone = byNumber["MSCU5551234"];
        gone.InYard.Should().BeFalse();
        gone.LastExitPurpose.Should().Be("EmptyRelease");
        gone.OpenRepairCaseId.Should().BeNull();

        var atFacility = byNumber["CMAU1234564"];
        atFacility.InYard.Should().BeFalse();
        atFacility.LastExitPurpose.Should().Be("ExternalRepair");
        atFacility.OutsideRepairFacility.Should().Be("Xưởng Cát Lái");
        atFacility.OutsideRepairStatus.Should().Be("GatedOut");
        atFacility.OpenRepairCaseId.Should().Be(outsideCase);

        var never = byNumber["CSQU3054383"];
        never.Should().Be(new ContainerSituation(
            false, null, null, null, null, null, null, null, null, null, null, null));
    }

    /*
        The gate-out page lists transfers waiting to leave (Status = Requested) and searches them by
        container number. Two transfers: MSCU1234566 still in the yard, CMAU1234564 already at the facility.
    */
    [Fact]
    public async Task External_Repair_List_Should_Filter_By_Status_And_Container_Number()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var waiting = await AddContainerAsync(dbContext, "MSCU1234566");
        var gone = await AddContainerAsync(dbContext, "CMAU1234564");
        foreach (var (container, bay) in new[] { (waiting, 1), (gone, 3) })
        {
            var caseId = (await GateInAsync(dbContext, seed, container, bay, condition: ContainerCondition.Damaged)).RepairCaseId!.Value;
            await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
            await RepairEstimateSteps.ApproveAsync(dbContext, caseId, new FixedTimeProvider(Now));
            var requested = await RequestExternalAsync(dbContext, caseId);
            if (container == gone)
                await GateOutForRepairAsync(dbContext, requested.Value);
        }
        var handler = new GetExternalRepairsQueryHandler(dbContext);

        var toLeave = await handler.HandleAsync(
            new GetExternalRepairsQuery(ExternalRepairStatus.Requested), TestContext.Current.CancellationToken);
        var bySearch = await handler.HandleAsync(
            new GetExternalRepairsQuery(Search: " cmau "), TestContext.Current.CancellationToken);
        var firstPage = await handler.HandleAsync(
            new GetExternalRepairsQuery(PageSize: 1), TestContext.Current.CancellationToken);

        // Waiting to leave: still in the yard, so its position is filled in
        var row = toLeave.Value!.Items.Should().ContainSingle().Subject;
        row.ContainerNumber.Should().Be("MSCU1234566");
        row.BlockCode.Should().Be("A");
        row.Facility.Should().Be("Xưởng Cát Lái");

        // Search ignores case and spaces; a container at the facility has no yard position
        var outside = bySearch.Value!.Items.Should().ContainSingle().Subject;
        outside.Status.Should().Be(ExternalRepairStatus.GatedOut);
        outside.BlockCode.Should().BeNull();

        firstPage.Value!.TotalCount.Should().Be(2);
        firstPage.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public void External_Repair_Validators_Should_Need_Facility_Reason_Reference_And_Vehicle()
    {
        var request = new RequestExternalRepairValidator();
        var validRequest = new RequestExternalRepairCommand(Guid.NewGuid(), "Xưởng Cát Lái", "Thay vách", "CMA-APP-0042");

        request.Validate(validRequest).IsValid.Should().BeTrue();
        request.Validate(validRequest with { Facility = " " }).IsValid.Should().BeFalse();
        request.Validate(validRequest with { Reference = new string('X', 101) }).IsValid.Should().BeFalse();
        request.Validate(validRequest with { CaseId = Guid.Empty }).IsValid.Should().BeFalse();

        var cancel = new CancelExternalRepairValidator();
        cancel.Validate(new CancelExternalRepairCommand(Guid.NewGuid(), "Sửa được tại bãi")).IsValid.Should().BeTrue();
        cancel.Validate(new CancelExternalRepairCommand(Guid.NewGuid(), "")).IsValid.Should().BeFalse();

        var leave = new GateOutForRepairValidator();
        leave.Validate(new GateOutForRepairCommand(Guid.NewGuid(), "51C-77777")).IsValid.Should().BeTrue();
        leave.Validate(new GateOutForRepairCommand(Guid.NewGuid(), new string('9', 21))).IsValid.Should().BeFalse();
        leave.Validate(new GateOutForRepairCommand(Guid.Empty, "51C-77777")).IsValid.Should().BeFalse();
    }

    /*
        Visit detail of a container sent for outside repair: the first visit keeps the operating assignment,
        the damage photo and the transfer that took it out; the visit after its return keeps the return.
    */
    [Fact]
    public async Task Visit_Detail_Should_List_Assignment_Photo_And_External_Repair_Documents()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var container = await AddContainerAsync(dbContext, "MSCU1234566");
        var first = await GateInAsync(dbContext, seed, container, bay: 1, condition: ContainerCondition.Damaged);
        var caseId = first.RepairCaseId!.Value;
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        await new UploadRepairPhotoCommandHandler(dbContext, new FakePhotoStorage(), new FixedTimeProvider(Now), new FakeCurrentUser())
            .HandleAsync(new UploadRepairPhotoCommand(caseId, null, "vach.png", "image/png", png.Length, new MemoryStream(png)),
                TestContext.Current.CancellationToken);
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        await RepairEstimateSteps.ApproveAsync(dbContext, caseId, new FixedTimeProvider(Now));
        var requested = await RequestExternalAsync(dbContext, caseId);
        await GateOutForRepairAsync(dbContext, requested.Value);
        var back = await GateInAsync(dbContext, seed, container, bay: 1);
        var handler = new GetVisitHistoryQueryHandler(dbContext, new FixedTimeProvider(Now));

        var outVisit = (await handler.HandleAsync(new GetVisitHistoryQuery(first.VisitId), TestContext.Current.CancellationToken)).Value!;
        var returnVisit = (await handler.HandleAsync(new GetVisitHistoryQuery(back.VisitId), TestContext.Current.CancellationToken)).Value!;

        outVisit.Documents.Select(d => d.Kind).Should().BeEquivalentTo(
            [VisitDocumentKind.OperatingAssignment, VisitDocumentKind.RepairPhoto, VisitDocumentKind.ExternalRepairOut]);
        outVisit.Documents.Should().Contain(d => d.Kind == VisitDocumentKind.ExternalRepairOut
            && d.Reference == "CMA-APP-0042 · Xưởng Cát Lái");
        outVisit.GateOut!.RepairFacility.Should().Be("Xưởng Cát Lái");
        returnVisit.Documents.Should().Contain(d => d.Kind == VisitDocumentKind.ExternalRepairReturn);
    }
}
