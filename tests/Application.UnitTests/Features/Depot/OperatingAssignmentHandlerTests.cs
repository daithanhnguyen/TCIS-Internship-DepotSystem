using FluentAssertions;
using TechSpherex.CleanArchitecture.Application.Features.ContainerVisits.GateIn;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.Cancel;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.Confirm;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.Create;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.End;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.GetActive;
using TechSpherex.CleanArchitecture.Application.Features.OperatingAssignments.GetForContainer;
using TechSpherex.CleanArchitecture.Domain.Common;
using TechSpherex.CleanArchitecture.Domain.Entities;
using TechSpherex.CleanArchitecture.Domain.Enums;
using TechSpherex.CleanArchitecture.Infrastructure.Persistence;
namespace TechSpherex.CleanArchitecture.Application.UnitTests.Features.Depot;

// Feedback 2: who operates a container, for which period, and how gate-in relies on it
public sealed class OperatingAssignmentHandlerTests
{
    // 21/10/2026 09:00 in Vietnam
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateOnly Today = new(2026, 10, 21);

    private sealed record Seed(Container Container, LineOperator Hmm, LineOperator Cma, LineOperator Triton);

    // HMMU2468109 owned by HMM; CMA operates; Triton is a leasing company with the owner role only
    private static async Task<Seed> SeedAsync(AppDbContext dbContext)
    {
        var hmm = DepotTestData.Partner("HMM");
        var seed = new Seed(
            new Container("HMMU2468109", "45R1", ContainerSize.Ft40, ContainerCategory.Reefer, 34000, 4800, null, "HMM"),
            hmm,
            DepotTestData.Partner("CMA"),
            new LineOperator("TRI", "Triton International", isOwner: true, isOperator: false));
        seed.Container.SetOwner(hmm);
        dbContext.AddRange(seed.Container, seed.Hmm, seed.Cma, seed.Triton);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return seed;
    }

    private static FixedTimeProvider Clock => new(Now);

    private static Task<Result<CreateOperatingAssignmentResponse>> CreateAsync(
        AppDbContext dbContext, Seed seed, LineOperator lineOperator, DateOnly from, DateOnly? to = null, bool confirm = true) =>
        new CreateOperatingAssignmentCommandHandler(dbContext, Clock).HandleAsync(
            new CreateOperatingAssignmentCommand(seed.Container.Id, lineOperator.Id, from, to, "HD-2026/015", null, confirm),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Create_Should_Record_A_Confirmed_Assignment_For_A_Rented_Container()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);

        var result = await CreateAsync(dbContext, seed, seed.Cma, new DateOnly(2026, 10, 1));
        var active = await new GetActiveAssignmentQueryHandler(dbContext, Clock)
            .HandleAsync(new GetActiveAssignmentQuery(seed.Container.Id), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        active.Value!.OperatorCode.Should().Be("CMA");
        active.Value.Status.Should().Be(OperatingAssignmentStatus.Confirmed);
        active.Value.Reference.Should().Be("HD-2026/015");
        seed.Container.OwnerId.Should().Be(seed.Hmm.Id); // the owner does not change
    }

    [Fact]
    public async Task Create_Should_Refuse_A_Partner_Without_The_Operator_Role()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);

        var result = await CreateAsync(dbContext, seed, seed.Triton, Today);

        result.Error!.Code.Should().Be("Partner.NotOperator");
        dbContext.OperatingAssignments.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_Should_Refuse_A_Period_Overlapping_Another_Confirmed_One()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        await CreateAsync(dbContext, seed, seed.Cma, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        // Confirmed at once: refused. Recorded as pending: allowed, but confirming it is refused.
        var direct = await CreateAsync(dbContext, seed, seed.Hmm, new DateOnly(2026, 10, 15));
        var pending = await CreateAsync(dbContext, seed, seed.Hmm, new DateOnly(2026, 10, 15), confirm: false);
        var confirm = await new ConfirmOperatingAssignmentCommandHandler(dbContext, Clock).HandleAsync(
            new ConfirmOperatingAssignmentCommand(pending.Value!.Id), TestContext.Current.CancellationToken);
        var afterIt = await CreateAsync(dbContext, seed, seed.Hmm, new DateOnly(2026, 11, 1));

        direct.Error!.Code.Should().Be("OperatingAssignment.Overlap");
        confirm.Error!.Code.Should().Be("OperatingAssignment.Overlap");
        afterIt.IsSuccess.Should().BeTrue(); // the next period starts the day after the first ends
    }

    [Fact]
    public async Task End_Should_Close_The_Period_But_Not_Before_A_Visit_That_Used_It()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var created = await CreateAsync(dbContext, seed, seed.Cma, new DateOnly(2026, 10, 1));
        var wait = Block.CreateVirtual("WAIT");
        dbContext.Blocks.Add(wait);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var gateIn = await new GateInCommandHandler(dbContext, Clock).HandleAsync(
            new GateInCommand(seed.Container.Id, seed.Cma.Id, wait.Id, null, null, null, ContainerCondition.Normal, null, "51C-12345"),
            TestContext.Current.CancellationToken);
        var handler = new EndOperatingAssignmentCommandHandler(dbContext, Clock);

        // The visit came in on 21/10, so the period cannot end on 20/10; ending on 21/10 is fine
        var tooEarly = await handler.HandleAsync(
            new EndOperatingAssignmentCommand(created.Value!.Id, new DateOnly(2026, 10, 20)), TestContext.Current.CancellationToken);
        var ended = await handler.HandleAsync(
            new EndOperatingAssignmentCommand(created.Value.Id, Today), TestContext.Current.CancellationToken);

        gateIn.IsSuccess.Should().BeTrue();
        tooEarly.Error!.Code.Should().Be("OperatingAssignment.UsedAfterEnd");
        ended.IsSuccess.Should().BeTrue();
        dbContext.OperatingAssignments.Single().ValidTo.Should().Be(Today);
        // Q5: the container in the yard keeps the operator of its visit
        dbContext.ContainerVisits.Single().LineOperatorId.Should().Be(seed.Cma.Id);
    }

    [Fact]
    public async Task Cancel_Should_Keep_The_Reason_And_Refuse_An_Assignment_Used_By_A_Visit()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var mistake = await CreateAsync(dbContext, seed, seed.Hmm, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var used = await CreateAsync(dbContext, seed, seed.Cma, new DateOnly(2026, 10, 1));
        var wait = Block.CreateVirtual("WAIT");
        dbContext.Blocks.Add(wait);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new GateInCommandHandler(dbContext, Clock).HandleAsync(
            new GateInCommand(seed.Container.Id, seed.Cma.Id, wait.Id, null, null, null, ContainerCondition.Normal, null, "51C-12345"),
            TestContext.Current.CancellationToken);
        var handler = new CancelOperatingAssignmentCommandHandler(dbContext, Clock);

        var cancelled = await handler.HandleAsync(
            new CancelOperatingAssignmentCommand(mistake.Value!.Id, "Nhập nhầm hãng"), TestContext.Current.CancellationToken);
        var inUse = await handler.HandleAsync(
            new CancelOperatingAssignmentCommand(used.Value!.Id, "Thử huỷ"), TestContext.Current.CancellationToken);
        var list = await new GetContainerAssignmentsQueryHandler(dbContext)
            .HandleAsync(new GetContainerAssignmentsQuery(seed.Container.Id), TestContext.Current.CancellationToken);

        cancelled.IsSuccess.Should().BeTrue();
        inUse.Error!.Code.Should().Be("OperatingAssignment.InUse");
        list.Value!.Select(a => a.OperatorCode).Should().Equal("CMA", "HMM"); // newest period first
        list.Value![0].VisitCount.Should().Be(1);
        list.Value[1].Status.Should().Be(OperatingAssignmentStatus.Cancelled);
        list.Value[1].CancelReason.Should().Be("Nhập nhầm hãng");
    }

    [Fact]
    public void Validators_Should_Check_Period_And_Reason()
    {
        var create = new CreateOperatingAssignmentValidator();
        var valid = new CreateOperatingAssignmentCommand(Guid.NewGuid(), Guid.NewGuid(), Today, null, null, null, true);

        create.Validate(valid).IsValid.Should().BeTrue();
        create.Validate(valid with { ValidTo = Today.AddDays(-1) }).IsValid.Should().BeFalse();
        create.Validate(valid with { OperatorId = Guid.Empty }).IsValid.Should().BeFalse();
        new CancelOperatingAssignmentValidator()
            .Validate(new CancelOperatingAssignmentCommand(Guid.NewGuid(), " ")).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_Should_Confirm_A_Pending_Assignment_Once_And_Report_Unknown_Ids()
    {
        await using var dbContext = TestDbContextFactory.Create();
        var seed = await SeedAsync(dbContext);
        var pending = await CreateAsync(dbContext, seed, seed.Cma, new DateOnly(2026, 10, 1), confirm: false);
        var handler = new ConfirmOperatingAssignmentCommandHandler(dbContext, Clock);

        var confirmed = await handler.HandleAsync(
            new ConfirmOperatingAssignmentCommand(pending.Value!.Id), TestContext.Current.CancellationToken);
        var again = await handler.HandleAsync(
            new ConfirmOperatingAssignmentCommand(pending.Value.Id), TestContext.Current.CancellationToken);
        var unknown = await handler.HandleAsync(
            new ConfirmOperatingAssignmentCommand(Guid.NewGuid()), TestContext.Current.CancellationToken);

        confirmed.IsSuccess.Should().BeTrue();
        var assignment = dbContext.OperatingAssignments.Single();
        assignment.Status.Should().Be(OperatingAssignmentStatus.Confirmed);
        assignment.ConfirmedAt.Should().Be(Now);
        again.Error!.Code.Should().Be("OperatingAssignment.NotPending");
        unknown.Error!.Code.Should().Be("OperatingAssignment.NotFound");
    }
}
