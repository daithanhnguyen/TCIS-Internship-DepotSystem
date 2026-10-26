using FluentAssertions;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GateIn;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Act;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Get;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.GetAll;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Photos;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Report;
using TechSpherex.CleanArchitecture.Application.Features.RepairCases.Shared;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Entities;
using TechSpherex.CleanArchitecture.Domain.Enums;
using TechSpherex.CleanArchitecture.Infrastructure.Persistence;
namespace TechSpherex.CleanArchitecture.Application.UnitTests.Features.Depot;

// Feedback 4: inspection & repair cases, from the damage report to the passed acceptance
public sealed class RepairCaseHandlerTests
{
    // 21/10/2026 09:00 in Vietnam
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(7));

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private sealed record Seed(LineOperator Msc, Block Yard, Container Dry, Container Reefer);

    // MSC operates both containers; a physical block that takes every type
    private static async Task<Seed> SeedAsync(AppDbContext dbContext)
    {
        var seed = new Seed(
            DepotTestData.Partner("MSC"),
            Block.CreatePhysical("A", maxBays: 10, maxRows: 4, maxTiers: 3),
            new Container("MSCU1234566", "22G1", ContainerSize.Ft20, ContainerCategory.Dry, 30480, 2200, null, "MSC"),
            new Container("MSCU5551234", "22R1", ContainerSize.Ft20, ContainerCategory.Reefer, 30480, 3000, null, "MSC"));
        dbContext.AddRange(seed.Msc, seed.Yard, seed.Dry, seed.Reefer,
            DepotTestData.Confirmed(seed.Dry, seed.Msc), DepotTestData.Confirmed(seed.Reefer, seed.Msc));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return seed;
    }

    private static FixedTimeProvider Clock => new(Now);

    private static Task<Result<GateInResponse>> GateInAsync(
        AppDbContext dbContext, Seed seed, Container container, int bay, ContainerCondition condition, string? note = null) =>
        new GateInCommandHandler(dbContext, Clock, new FakeCurrentUser()).HandleAsync(
            new GateInCommand(container.Id, seed.Msc.Id, seed.Yard.Id, bay, 1, 1, condition, note, "51C-12345"),
            TestContext.Current.CancellationToken);

    private static Task<Result<RecordedDamage>> ReportAsync(
        AppDbContext dbContext, Container container, string description) =>
        new ReportDamageCommandHandler(dbContext, Clock, new FakeCurrentUser()).HandleAsync(
            new ReportDamageCommand(container.Id, description, RepairFindingSource.Yard), TestContext.Current.CancellationToken);

    private static Task<Result> ActAsync(AppDbContext dbContext, Guid caseId, RepairAction action, string? note = null) =>
        new ApplyRepairActionCommandHandler(dbContext, Clock, new FakeCurrentUser())
            .HandleAsync(new ApplyRepairActionCommand(caseId, action, note), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Gate_In_Damaged_Should_Open_A_Case_Awaiting_Inspection_And_Keep_The_Position()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);

        var result = await GateInAsync(dbContext, seed, seed.Dry, 3, ContainerCondition.Damaged, "Móp cửa");

        var repairCase = dbContext.RepairCases.Single();
        result.Value!.RepairCaseId.Should().Be(repairCase.Id);
        repairCase.Status.Should().Be(RepairCaseStatus.AwaitingInspection);
        repairCase.VisitId.Should().Be(dbContext.ContainerVisits.Single().Id);
        dbContext.RepairFindings.Single().Source.Should().Be(RepairFindingSource.GateIn);
        seed.Dry.Condition.Should().Be(ContainerCondition.Damaged);
        // Not moved to a virtual block: it stays where it was placed
        dbContext.ContainerVisits.Single().BlockId.Should().Be(seed.Yard.Id);
    }

    [Fact]
    public async Task Gate_In_Normal_Should_Not_Open_A_Case_And_Damaged_Needs_A_Description()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);

        var result = await GateInAsync(dbContext, seed, seed.Dry, 3, ContainerCondition.Normal);
        var validation = new GateInValidator().Validate(
            new GateInCommand(seed.Reefer.Id, seed.Msc.Id, seed.Yard.Id, 5, 1, 1, ContainerCondition.Damaged, " ", "51C-12345"));

        result.Value!.RepairCaseId.Should().BeNull();
        dbContext.RepairCases.Should().BeEmpty();
        validation.Errors.Should().ContainSingle(e => e.PropertyName == "ConditionNote");
    }

    [Fact]
    public async Task Reporting_Twice_Should_Add_To_The_Same_Open_Case()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        await GateInAsync(dbContext, seed, seed.Dry, 3, ContainerCondition.Damaged, "Móp cửa");

        var second = await ReportAsync(dbContext, seed.Dry, "Thủng sàn");

        second.Value!.Created.Should().BeFalse();
        dbContext.RepairCases.Should().ContainSingle();
        dbContext.RepairFindings.Select(f => f.Description).Should().BeEquivalentTo("Móp cửa", "Thủng sàn");
        dbContext.RepairCaseEvents.Select(e => e.Kind).Should().BeEquivalentTo([RepairEventKind.Opened, RepairEventKind.FindingAdded]);
    }

    [Fact]
    public async Task Full_Flow_Should_Need_Acceptance_And_Keep_History()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var opened = await ReportAsync(dbContext, seed.Reefer, "Hỏng máy lạnh");
        var id = opened.Value!.CaseId;

        await ActAsync(dbContext, id, RepairAction.InspectNeedsRepair, "Thay máy nén");
        await RepairEstimateSteps.ApproveAsync(dbContext, id, Clock);
        await ActAsync(dbContext, id, RepairAction.StartRepair);
        await ActAsync(dbContext, id, RepairAction.CompleteRepair);
        // The repairer saying "done" is not the acceptance: still blocked, then sent back
        var noReason = await ActAsync(dbContext, id, RepairAction.AcceptRejected);
        await ActAsync(dbContext, id, RepairAction.AcceptRejected, "Còn rò ga");
        await ActAsync(dbContext, id, RepairAction.CompleteRepair);
        var passed = await ActAsync(dbContext, id, RepairAction.AcceptPassed);

        noReason.Error!.Code.Should().Be("RepairCase.ReasonRequired");
        passed.IsSuccess.Should().BeTrue();
        var repairCase = dbContext.RepairCases.Single();
        repairCase.Status.Should().Be(RepairCaseStatus.Passed);
        repairCase.RepairNeeded.Should().BeTrue();
        repairCase.ClosedAt.Should().Be(Now);
        seed.Reefer.Condition.Should().Be(ContainerCondition.Normal);
        // opened + 6 actions + estimate created, submitted, approved
        dbContext.RepairCaseEvents.Should().HaveCount(10);
        dbContext.RepairCaseEvents.Should().OnlyContain(e => e.By == "staff@depot.test");
    }

    [Fact]
    public async Task Action_Out_Of_Order_Should_Return_Conflict()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var opened = await ReportAsync(dbContext, seed.Dry, "Móp cửa");

        var skip = await ActAsync(dbContext, opened.Value!.CaseId, RepairAction.AcceptPassed);
        var missing = await ActAsync(dbContext, Guid.NewGuid(), RepairAction.InspectPassed);

        skip.Error!.Code.Should().Be("RepairCase.InvalidTransition");
        missing.Error!.Type.Should().Be(ErrorType.NotFound);
        dbContext.RepairCases.Single().Status.Should().Be(RepairCaseStatus.AwaitingInspection);
    }

    [Fact]
    public async Task A_Report_After_The_Case_Passed_Should_Open_A_New_Case()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var first = await ReportAsync(dbContext, seed.Dry, "Trầy sơn");
        await ActAsync(dbContext, first.Value!.CaseId, RepairAction.InspectPassed, "Không ảnh hưởng");

        var second = await ReportAsync(dbContext, seed.Dry, "Móp nóc");

        second.Value!.Created.Should().BeTrue();
        second.Value.CaseId.Should().NotBe(first.Value.CaseId);
        dbContext.RepairCases.Count(c => c.Status != RepairCaseStatus.Passed).Should().Be(1);
    }

    [Fact]
    public async Task List_Should_Filter_And_Detail_Should_Offer_The_Next_Actions()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        await GateInAsync(dbContext, seed, seed.Dry, 3, ContainerCondition.Damaged, "Móp cửa");
        var reefer = await ReportAsync(dbContext, seed.Reefer, "Hỏng máy lạnh");
        await ActAsync(dbContext, reefer.Value!.CaseId, RepairAction.InspectPassed);
        var list = new GetRepairCasesQueryHandler(dbContext);

        var open = await list.HandleAsync(new GetRepairCasesQuery(Open: true), TestContext.Current.CancellationToken);
        var byOperator = await list.HandleAsync(new GetRepairCasesQuery(OperatorId: seed.Msc.Id), TestContext.Current.CancellationToken);
        var bySearch = await list.HandleAsync(new GetRepairCasesQuery(Search: "5551"), TestContext.Current.CancellationToken);
        var outOfRange = await list.HandleAsync(
            new GetRepairCasesQuery(ReportedFrom: new DateOnly(2026, 10, 22)), TestContext.Current.CancellationToken);
        var detail = await new GetRepairCaseQueryHandler(dbContext)
            .HandleAsync(new GetRepairCaseQuery(open.Value!.Items[0].Id), TestContext.Current.CancellationToken);

        open.Value.Items.Should().ContainSingle(c => c.ContainerNumber == "MSCU1234566");
        open.Value.Items[0].BlockCode.Should().Be("A");
        open.Value.Items[0].OperatorCode.Should().Be("MSC");
        byOperator.Value!.TotalCount.Should().Be(1); // the reefer report was made outside a visit
        bySearch.Value!.Items.Should().ContainSingle(c => c.Status == RepairCaseStatus.Passed);
        outOfRange.Value!.TotalCount.Should().Be(0);
        detail.Value!.AllowedActions.Should().BeEquivalentTo([RepairAction.InspectPassed, RepairAction.InspectNeedsRepair]);
        detail.Value.ConditionAtGateIn.Should().Be(ContainerCondition.Damaged);
        detail.Value.Findings.Should().ContainSingle(f => f.Description == "Móp cửa");
        detail.Value.InYard.Should().BeTrue();
    }

    [Fact]
    public async Task Photo_Should_Be_A_Real_Png_Or_Jpg_On_An_Open_Case()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var opened = await ReportAsync(dbContext, seed.Dry, "Móp cửa");
        var storage = new FakePhotoStorage();
        var upload = new UploadRepairPhotoCommandHandler(dbContext, storage, Clock, new FakeCurrentUser());
        var caseId = opened.Value!.CaseId;

        var png = await upload.HandleAsync(
            new UploadRepairPhotoCommand(caseId, opened.Value.FindingId, "cua.png", "image/png", PngBytes.Length, new MemoryStream(PngBytes)),
            TestContext.Current.CancellationToken);
        var renamedText = await upload.HandleAsync(
            new UploadRepairPhotoCommand(caseId, null, "note.png", "image/png", 5, new MemoryStream("hello"u8.ToArray())),
            TestContext.Current.CancellationToken);
        var download = await new GetRepairPhotoQueryHandler(dbContext, storage)
            .HandleAsync(new GetRepairPhotoQuery(png.Value!.Id), TestContext.Current.CancellationToken);
        await ActAsync(dbContext, caseId, RepairAction.InspectPassed);
        var onClosed = await upload.HandleAsync(
            new UploadRepairPhotoCommand(caseId, null, "late.png", "image/png", PngBytes.Length, new MemoryStream(PngBytes)),
            TestContext.Current.CancellationToken);

        png.Value.FindingId.Should().Be(opened.Value.FindingId);
        renamedText.Error!.Code.Should().Be("RepairPhoto.InvalidFile");
        download.Value!.ContentType.Should().Be("image/png");
        onClosed.Error!.Code.Should().Be("RepairCase.Closed");
        storage.Files.Should().ContainSingle();
    }

    [Fact]
    public void Photo_Validator_Should_Reject_Other_Types_And_Large_Files()
    {
        var validator = new UploadRepairPhotoValidator();
        var valid = new UploadRepairPhotoCommand(Guid.NewGuid(), null, "a.jpg", "image/jpeg", 1000, Stream.Null);

        validator.Validate(valid).IsValid.Should().BeTrue();
        validator.Validate(valid with { ContentType = "application/pdf" }).IsValid.Should().BeFalse();
        validator.Validate(valid with { Length = 5 * 1024 * 1024 + 1 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Action_And_Report_Validators_Should_Check_Enums_Note_And_Source()
    {
        var action = new ApplyRepairActionValidator();
        action.Validate(new ApplyRepairActionCommand(Guid.NewGuid(), RepairAction.StartRepair, null)).IsValid.Should().BeTrue();
        action.Validate(new ApplyRepairActionCommand(Guid.NewGuid(), (RepairAction)99, null)).IsValid.Should().BeFalse();
        action.Validate(new ApplyRepairActionCommand(Guid.NewGuid(), RepairAction.AcceptRejected, new string('x', 501)))
            .IsValid.Should().BeFalse();

        var report = new ReportDamageValidator();
        var valid = new ReportDamageCommand(Guid.NewGuid(), "Móp cửa", RepairFindingSource.Yard);
        report.Validate(valid).IsValid.Should().BeTrue();
        report.Validate(valid with { Description = " " }).IsValid.Should().BeFalse();
        // Gate-in records its damage through the gate-in itself, never through this command
        report.Validate(valid with { Source = RepairFindingSource.GateIn }).Errors
            .Should().ContainSingle(e => e.PropertyName == "Source");
    }
}
