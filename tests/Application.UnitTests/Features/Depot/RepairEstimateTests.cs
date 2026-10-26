using FluentAssertions;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Act;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Estimates;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Get;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.GetAll;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Report;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Shared;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Entities;
using TechSpherex.CleanArchitecture.Domain.Enums;
using TechSpherex.CleanArchitecture.Infrastructure.Persistence;
namespace TechSpherex.CleanArchitecture.Application.UnitTests.Features.Depot;

// Feedback 4: repair costs (estimate versions, approval of the repair through the estimate, who pays)
public sealed class RepairEstimateTests
{
    // 22/10/2026 10:00 in Vietnam
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(7));

    private static FixedTimeProvider Clock => new(Now);
    private static readonly FakeCurrentUser User = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // One container with an open case (damage reported in the yard), not inspected yet
    private static async Task<Guid> OpenCaseAsync(AppDbContext dbContext, string number = "MSCU1234566")
    {
        var msc = dbContext.LineOperators.FirstOrDefault(o => o.Code == "MSC") ?? DepotTestData.Partner("MSC");
        var container = new Container(number, "22G1", ContainerSize.Ft20, ContainerCategory.Dry, 30480, 2200, null, "MSC");
        // The second case of a test reuses the operator saved by the first
        if (dbContext.Entry(msc).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            dbContext.Add(msc);
        dbContext.AddRange(container, DepotTestData.Confirmed(container, msc));
        await dbContext.SaveChangesAsync(Token);

        var reported = await new ReportDamageCommandHandler(dbContext, Clock, User)
            .HandleAsync(new ReportDamageCommand(container.Id, "Móp cửa", RepairFindingSource.Yard), Token);
        return reported.Value!.CaseId;
    }

    private static Task<Result> ActAsync(AppDbContext dbContext, Guid caseId, RepairAction action) =>
        new ApplyRepairActionCommandHandler(dbContext, Clock, User)
            .HandleAsync(new ApplyRepairActionCommand(caseId, action, null), Token);

    private static Task<Result<Guid>> CreateAsync(AppDbContext dbContext, Guid caseId) =>
        new CreateRepairEstimateCommandHandler(dbContext, Clock, User)
            .HandleAsync(new CreateRepairEstimateCommand(caseId, "vnd", "Theo EIR", null), Token);

    private static Task<Result> SaveAsync(AppDbContext dbContext, Guid estimateId, params RepairEstimateLineInput[] lines) =>
        new UpdateRepairEstimateCommandHandler(dbContext)
            .HandleAsync(new UpdateRepairEstimateCommand(estimateId, "VND", "Theo EIR", "EIR-001", lines), Token);

    private static Task<Result> SubmitAsync(AppDbContext dbContext, Guid estimateId) =>
        new SubmitRepairEstimateCommandHandler(dbContext, Clock, User)
            .HandleAsync(new SubmitRepairEstimateCommand(estimateId), Token);

    private static Task<Result> DecideAsync(
        AppDbContext dbContext, Guid estimateId, RepairEstimateDecision decision, string? party = "HMM", string? note = null) =>
        new DecideRepairEstimateCommandHandler(dbContext, Clock, User)
            .HandleAsync(new DecideRepairEstimateCommand(estimateId, decision, party, "Mr. Lee", "Email 22/10", note), Token);

    private static Task<Result> SetPayerAsync(
        AppDbContext dbContext, Guid estimateId, Guid lineId, CostPayer payer, string? name, CostPayerStatus status, string? note) =>
        new SetEstimateLinePayerCommandHandler(dbContext, Clock, User)
            .HandleAsync(new SetEstimateLinePayerCommand(estimateId, lineId, payer, name, status, note), Token);

    private static async Task<RepairCaseDetail> DetailAsync(AppDbContext dbContext, Guid caseId) =>
        (await new GetRepairCaseQueryHandler(dbContext).HandleAsync(new GetRepairCaseQuery(caseId), Token)).Value!;

    private static RepairEstimateLineInput Door(decimal amount = 1_500_000m, CostPayer payer = CostPayer.LineOperator) =>
        new(null, "Móp cửa", "Nắn, sơn lại", amount, payer, null);

    private static RepairEstimateLineInput Floor(decimal amount = 3_000_000m) =>
        new(null, "Thủng sàn", "Thay ván sàn", amount, CostPayer.Customer, null);

    [Fact]
    public async Task Repair_Should_Only_Be_Approved_Through_An_Estimate()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var caseId = await OpenCaseAsync(dbContext);

        var beforeInspection = await CreateAsync(dbContext, caseId);
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        var direct = await ActAsync(dbContext, caseId, RepairAction.ApproveRepair);
        var detail = await DetailAsync(dbContext, caseId);

        beforeInspection.Error!.Code.Should().Be("RepairEstimate.NotInspected");
        direct.Error!.Code.Should().Be("RepairCase.EstimateRequired");
        detail.Status.Should().Be(RepairCaseStatus.AwaitingEstimateApproval);
        detail.AllowedActions.Should().BeEmpty();
        detail.CanCreateEstimate.Should().BeTrue();
    }

    [Fact]
    public async Task Rejected_Version_Should_Keep_The_Case_Waiting_And_The_Next_Version_Copies_Its_Items()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var caseId = await OpenCaseAsync(dbContext);
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);

        var v1 = (await CreateAsync(dbContext, caseId)).Value;
        var emptySubmit = await SubmitAsync(dbContext, v1);
        await SaveAsync(dbContext, v1, Door(), Floor());
        var whileOpen = await CreateAsync(dbContext, caseId);
        await SubmitAsync(dbContext, v1);
        var editSubmitted = await SaveAsync(dbContext, v1, Door());
        var noReason = await DecideAsync(dbContext, v1, RepairEstimateDecision.Reject);
        var rejected = await DecideAsync(dbContext, v1, RepairEstimateDecision.Reject, note: "Giá sơn quá cao");
        var statusAfterReject = dbContext.RepairCases.Single().Status;

        var v2 = (await CreateAsync(dbContext, caseId)).Value;
        await SaveAsync(dbContext, v2, Door(1_200_000m), Floor());
        await SubmitAsync(dbContext, v2);
        var approved = await DecideAsync(dbContext, v2, RepairEstimateDecision.Approve);
        var detail = await DetailAsync(dbContext, caseId);

        emptySubmit.Error!.Code.Should().Be("RepairEstimate.NoLines");
        whileOpen.Error!.Code.Should().Be("RepairEstimate.OpenVersion");
        editSubmitted.Error!.Code.Should().Be("RepairEstimate.NotDraft");
        noReason.Error!.Code.Should().Be("RepairEstimate.ReasonRequired");
        rejected.IsSuccess.Should().BeTrue();
        statusAfterReject.Should().Be(RepairCaseStatus.AwaitingEstimateApproval);
        approved.IsSuccess.Should().BeTrue();

        detail.Status.Should().Be(RepairCaseStatus.RepairApproved);
        detail.AllowedActions.Should().BeEquivalentTo([RepairAction.StartRepair]);
        detail.Estimates.Select(e => e.Version).Should().Equal(2, 1); // newest first
        var latest = detail.Estimates[0];
        latest.Currency.Should().Be("VND");
        latest.Total.Should().Be(4_200_000m);
        latest.ApproverParty.Should().Be("HMM");
        latest.PayerTotals.Should().BeEquivalentTo([
            new PayerTotal(CostPayer.LineOperator, null, 1_200_000m),
            new PayerTotal(CostPayer.Customer, null, 3_000_000m)]);
        // Version 1 stays as it was decided
        detail.Estimates[1].Status.Should().Be(RepairEstimateStatus.Rejected);
        detail.Estimates[1].Total.Should().Be(4_500_000m);
        detail.Estimates[1].DecisionNote.Should().Be("Giá sơn quá cao");

        var approvedEvent = dbContext.RepairCaseEvents.Single(e => e.Kind == RepairEventKind.EstimateApproved);
        approvedEvent.FromStatus.Should().Be(RepairCaseStatus.AwaitingEstimateApproval);
        approvedEvent.ToStatus.Should().Be(RepairCaseStatus.RepairApproved);
        approvedEvent.Note.Should().Be("v2 · 4200000 VND · HMM");
        dbContext.RepairCaseEvents.Select(e => e.Kind).Should().Contain(
            [RepairEventKind.EstimateCreated, RepairEventKind.EstimateSubmitted, RepairEventKind.EstimateRejected]);
    }

    [Fact]
    public async Task Extra_Version_While_Repairing_Should_Not_Change_The_Status()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var caseId = await OpenCaseAsync(dbContext);
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        await RepairEstimateSteps.ApproveAsync(dbContext, caseId, Clock);
        await ActAsync(dbContext, caseId, RepairAction.StartRepair);

        // Extra damage found while repairing: version 2 starts from version 1 and adds an item
        var v2 = (await CreateAsync(dbContext, caseId)).Value;
        var copied = dbContext.RepairEstimates.Single(e => e.Id == v2).Lines.Count;
        await SaveAsync(dbContext, v2, Door(2_000_000m), Floor(500_000m));
        await SubmitAsync(dbContext, v2);
        var changes = await DecideAsync(dbContext, v2, RepairEstimateDecision.RequestChanges, note: "Ghi rõ vị trí ván sàn");
        var v3 = (await CreateAsync(dbContext, caseId)).Value;
        await SubmitAsync(dbContext, v3);
        await DecideAsync(dbContext, v3, RepairEstimateDecision.Approve, party: "HMM");
        var detail = await DetailAsync(dbContext, caseId);

        copied.Should().Be(1);
        changes.IsSuccess.Should().BeTrue();
        detail.Status.Should().Be(RepairCaseStatus.InRepair);
        detail.Estimates.Select(e => e.Status).Should().Equal(
            RepairEstimateStatus.Approved, RepairEstimateStatus.ChangesRequested, RepairEstimateStatus.Approved);
        detail.Estimates[0].Total.Should().Be(2_500_000m);
        // First approval moved the case; the later one is only recorded
        dbContext.RepairCaseEvents.Where(e => e.Kind == RepairEventKind.EstimateApproved)
            .Select(e => new { e.FromStatus, e.ToStatus })
            .Should().BeEquivalentTo([
                new { FromStatus = (RepairCaseStatus?)RepairCaseStatus.AwaitingEstimateApproval, ToStatus = RepairCaseStatus.RepairApproved },
                new { FromStatus = (RepairCaseStatus?)RepairCaseStatus.InRepair, ToStatus = RepairCaseStatus.InRepair }]);
    }

    [Fact]
    public async Task Payer_Should_Follow_Its_Rules_And_The_List_Should_Filter_By_It()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var caseId = await OpenCaseAsync(dbContext);
        var otherCase = await OpenCaseAsync(dbContext, "MSCU5551234");
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        var estimateId = await RepairEstimateSteps.ApproveAsync(dbContext, caseId, Clock);
        var lineId = dbContext.RepairEstimateLines.Single().Id;

        var otherNoName = await SetPayerAsync(dbContext, estimateId, lineId, CostPayer.Other, null, CostPayerStatus.Expected, null);
        var disputeNoReason = await SetPayerAsync(dbContext, estimateId, lineId, CostPayer.Owner, null, CostPayerStatus.Disputed, null);
        var agreedUnknown = await SetPayerAsync(dbContext, estimateId, lineId, CostPayer.Undetermined, null, CostPayerStatus.Agreed, null);
        var unknownLine = await SetPayerAsync(dbContext, estimateId, Guid.NewGuid(), CostPayer.Owner, null, CostPayerStatus.Expected, null);
        var disputed = await SetPayerAsync(dbContext, estimateId, lineId, CostPayer.Owner, "Triton", CostPayerStatus.Disputed,
            "Hãng tàu cho rằng hư từ trước");

        var list = new GetRepairCasesQueryHandler(dbContext);
        var byDispute = await list.HandleAsync(new GetRepairCasesQuery(PayerStatus: CostPayerStatus.Disputed), Token);
        var byOwner = await list.HandleAsync(new GetRepairCasesQuery(Payer: CostPayer.Owner), Token);
        var byCustomer = await list.HandleAsync(new GetRepairCasesQuery(Payer: CostPayer.Customer), Token);
        var all = await list.HandleAsync(new GetRepairCasesQuery(), Token);

        otherNoName.Error!.Code.Should().Be("RepairEstimate.PayerNameRequired");
        disputeNoReason.Error!.Code.Should().Be("RepairEstimate.DisputeReasonRequired");
        agreedUnknown.Error!.Code.Should().Be("RepairEstimate.PayerUndetermined");
        unknownLine.Error!.Code.Should().Be("RepairEstimate.LineNotFound");
        disputed.IsSuccess.Should().BeTrue();

        var line = dbContext.RepairEstimateLines.Single();
        line.Amount.Should().Be(2_000_000m); // the amount never changes with the payer
        line.PayerNote.Should().Be("Hãng tàu cho rằng hư từ trước");
        dbContext.RepairCaseEvents.Single(e => e.Kind == RepairEventKind.PayerChanged).Note
            .Should().Be("v1 #1: Owner (Triton) · Disputed · Hãng tàu cho rằng hư từ trước");
        byDispute.Value!.Items.Should().ContainSingle(c => c.Id == caseId);
        byOwner.Value!.Items.Should().ContainSingle(c => c.Id == caseId);
        byCustomer.Value!.Items.Should().BeEmpty();
        var row = all.Value!.Items.Single(c => c.Id == caseId);
        row.EstimateVersion.Should().Be(1);
        row.EstimateStatus.Should().Be(RepairEstimateStatus.Approved);
        row.EstimateTotal.Should().Be(2_000_000m);
        row.EstimateCurrency.Should().Be("VND");
        all.Value.Items.Single(c => c.Id == otherCase).EstimateVersion.Should().BeNull();
    }

    [Fact]
    public async Task Items_Should_Only_Name_Damages_Of_Their_Case()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var caseId = await OpenCaseAsync(dbContext);
        var otherCase = await OpenCaseAsync(dbContext, "MSCU5551234");
        await ActAsync(dbContext, caseId, RepairAction.InspectNeedsRepair);
        var ownFinding = dbContext.RepairFindings.Single(f => f.RepairCaseId == caseId).Id;
        var otherFinding = dbContext.RepairFindings.Single(f => f.RepairCaseId == otherCase).Id;
        var estimateId = (await CreateAsync(dbContext, caseId)).Value;

        var wrong = await SaveAsync(dbContext, estimateId, Door() with { FindingId = otherFinding });
        var right = await SaveAsync(dbContext, estimateId, Door() with { FindingId = ownFinding });

        wrong.Error!.Code.Should().Be("RepairEstimate.UnknownFinding");
        right.IsSuccess.Should().BeTrue();
        dbContext.RepairEstimateLines.Single().FindingId.Should().Be(ownFinding);
    }

    [Fact]
    public void Validators_Should_Check_Currency_Amounts_And_Decision_Fields()
    {
        var update = new UpdateRepairEstimateValidator().Validate(new UpdateRepairEstimateCommand(
            Guid.NewGuid(), "EUR", null, null, [Door(-1m), new RepairEstimateLineInput(null, " ", "Sơn", 10m, CostPayer.Depot, null)]));
        var approveWithoutParty = new DecideRepairEstimateValidator().Validate(
            new DecideRepairEstimateCommand(Guid.NewGuid(), RepairEstimateDecision.Approve, " ", null, null, null));
        var rejectWithoutReason = new DecideRepairEstimateValidator().Validate(
            new DecideRepairEstimateCommand(Guid.NewGuid(), RepairEstimateDecision.Reject, null, null, null, null));
        var create = new CreateRepairEstimateValidator().Validate(new CreateRepairEstimateCommand(Guid.NewGuid(), "usd", null, null));

        update.Errors.Select(e => e.PropertyName).Should().Contain(
            ["Currency", "Lines[0].Amount", "Lines[1].DamageDescription"]);
        approveWithoutParty.Errors.Should().ContainSingle(e => e.PropertyName == "ApproverParty");
        rejectWithoutReason.Errors.Should().ContainSingle(e => e.PropertyName == "Note");
        create.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Payer_Validator_Should_Check_Ids_Enums_And_Lengths()
    {
        var validator = new SetEstimateLinePayerValidator();
        var valid = new SetEstimateLinePayerCommand(
            Guid.NewGuid(), Guid.NewGuid(), CostPayer.LineOperator, "CMA CGM", CostPayerStatus.Agreed, null);

        validator.Validate(valid).IsValid.Should().BeTrue();
        validator.Validate(valid with { LineId = Guid.Empty }).IsValid.Should().BeFalse();
        validator.Validate(valid with { Payer = (CostPayer)99 }).IsValid.Should().BeFalse();
        validator.Validate(valid with { Status = (CostPayerStatus)99 }).IsValid.Should().BeFalse();
        validator.Validate(valid with { PayerName = new string('x', 201) }).IsValid.Should().BeFalse();
    }
}
