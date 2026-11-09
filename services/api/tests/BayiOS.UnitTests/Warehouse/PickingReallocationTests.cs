using BayiOS.Modules.Warehouse.Domain;

namespace BayiOS.UnitTests.Warehouse;

/// <summary>
/// #98: when an order line's reservation moves shelves, only the open quantity of the active pick task follows;
/// picked / short quantities stay, and a task keeps one line per (order line, shelf).
/// </summary>
public class PickingReallocationTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OrderLine = Guid.NewGuid();
    private static readonly Guid Variant = Guid.NewGuid();
    private static readonly Guid ShelfA = Guid.NewGuid();
    private static readonly Guid ShelfB = Guid.NewGuid();
    private static readonly Guid ShelfC = Guid.NewGuid();

    private static (PickingTask Task, PickingTaskLine Line) TaskWithLine(decimal toPick, Guid? shelf = null)
    {
        var task = PickingTask.Create(Tenant, PickingTaskTypes.Pick, Guid.NewGuid(), "SIP-2026-000001", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var line = PickingTaskLine.Create(task, 1, OrderLine, Variant, "LED-0001", "Sentetik LED far", "adet", shelf ?? ShelfA, toPick);
        task.Lines.Add(line);
        return (task, line);
    }

    [Fact]
    public void An_untouched_line_moving_whole_just_changes_its_shelf()
    {
        var (task, line) = TaskWithLine(5m);

        var outcome = PickingReallocation.Apply(task, OrderLine, ShelfA, ShelfB, 5m);

        Assert.Equal(5m, outcome.Moved);
        Assert.Empty(outcome.Added);
        Assert.Empty(outcome.Emptied);
        Assert.Equal((ShelfB, 5m), (Assert.Single(task.Lines).LocationId!.Value, line.QtyToPick));
    }

    [Fact]
    public void A_partly_picked_line_keeps_its_picks_and_the_open_rest_moves_to_a_new_line()
    {
        var (task, line) = TaskWithLine(5m);
        line.QtyPicked = 2m;
        line.QtyShort = 1m;

        var outcome = PickingReallocation.Apply(task, OrderLine, ShelfA, ShelfB, 10m);

        Assert.Equal(2m, outcome.Moved); // only the open quantity; the reservation beyond the task is not the task's business
        var added = Assert.Single(outcome.Added);
        Assert.Equal((ShelfB, 2m, 0m, 2), (added.LocationId!.Value, added.QtyToPick, added.QtyPicked, added.Sequence));
        Assert.Equal((ShelfA, 3m, 2m, 1m, 0m), (line.LocationId!.Value, line.QtyToPick, line.QtyPicked, line.QtyShort, line.Remaining));
        Assert.Empty(outcome.Emptied);
    }

    [Fact]
    public void Moving_into_an_existing_line_merges_and_keeps_the_emptied_source_as_an_empty_line()
    {
        var (task, source) = TaskWithLine(3m);
        var target = PickingTaskLine.Create(task, 2, OrderLine, Variant, "LED-0001", "Sentetik LED far", "adet", ShelfB, 4m);
        target.QtyPicked = 1m;
        task.Lines.Add(target);

        var outcome = PickingReallocation.Apply(task, OrderLine, ShelfA, ShelfB, 3m);

        Assert.Equal(3m, outcome.Moved);
        Assert.Empty(outcome.Added);
        Assert.Same(source, Assert.Single(outcome.Emptied));
        Assert.Equal(2, task.Lines.Count); // never deleted: the application role has no DELETE on task lines
        Assert.Equal((0m, ShelfA), (source.QtyToPick, source.LocationId!.Value));
        Assert.Equal((7m, 1m, 6m), (target.QtyToPick, target.QtyPicked, target.Remaining));

        // Moving back reuses the empty line instead of adding a second line for (order line, shelf A).
        var back = PickingReallocation.Apply(task, OrderLine, ShelfB, ShelfA, 2m);
        Assert.Empty(back.Added);
        Assert.Equal((2m, 5m), (source.QtyToPick, target.QtyToPick));
    }

    [Fact]
    public void Partial_move_splits_the_line_and_the_total_to_pick_is_unchanged()
    {
        var (task, line) = TaskWithLine(6m);

        var outcome = PickingReallocation.Apply(task, OrderLine, ShelfA, ShelfC, 2m);

        Assert.Equal(2m, outcome.Moved);
        Assert.Equal(6m, task.Lines.Sum(l => l.QtyToPick));
        Assert.Equal(4m, line.QtyToPick);
        Assert.Equal((ShelfC, 2m), (outcome.Added[0].LocationId!.Value, outcome.Added[0].QtyToPick));
    }

    [Theory]
    [InlineData(PickingTaskStatuses.Completed)]
    [InlineData(PickingTaskStatuses.CompletedWithShortage)]
    [InlineData(PickingTaskStatuses.Cancelled)]
    public void Finished_tasks_are_never_changed(string status)
    {
        var (task, line) = TaskWithLine(5m);
        task.Status = status;

        Assert.Same(LineReallocation.None, PickingReallocation.Apply(task, OrderLine, ShelfA, ShelfB, 5m));
        Assert.Equal(ShelfA, line.LocationId);
    }

    [Fact]
    public void Other_order_lines_shelves_and_putaway_tasks_are_untouched()
    {
        var (task, line) = TaskWithLine(5m);
        Assert.Equal(0m, PickingReallocation.Apply(task, Guid.NewGuid(), ShelfA, ShelfB, 5m).Moved);
        Assert.Equal(0m, PickingReallocation.Apply(task, OrderLine, ShelfC, ShelfB, 5m).Moved);
        Assert.Equal(0m, PickingReallocation.Apply(task, OrderLine, ShelfA, ShelfA, 5m).Moved);
        Assert.Equal((ShelfA, 5m), (line.LocationId!.Value, line.QtyToPick));

        var putaway = PickingTask.Create(Tenant, PickingTaskTypes.Putaway, Guid.NewGuid(), "SIP-2026-000002", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        putaway.Lines.Add(PickingTaskLine.Create(putaway, 1, OrderLine, Variant, "LED-0001", "Sentetik LED far", "adet", ShelfA, 2m));
        Assert.Equal(0m, PickingReallocation.Apply(putaway, OrderLine, ShelfA, ShelfB, 2m).Moved);
    }

    [Fact]
    public void A_line_never_loses_picked_or_short_quantity()
    {
        var (_, line) = TaskWithLine(3m);
        line.QtyPicked = 3m;

        Assert.Throws<InvalidOperationException>(() => line.ChangeQtyToPick(-1m));
        Assert.Throws<InvalidOperationException>(() => line.MoveTo(ShelfB));
    }

    [Theory]
    [InlineData(PickingTaskPriorities.Urgent, PickingTaskPriorities.High)]
    [InlineData(PickingTaskPriorities.High, PickingTaskPriorities.Normal)]
    [InlineData(PickingTaskPriorities.Normal, PickingTaskPriorities.Low)]
    public void Priority_ranks_order_the_work_queue(string higher, string lower) =>
        Assert.True(PickingTaskPriorities.Rank(higher) > PickingTaskPriorities.Rank(lower));
}
