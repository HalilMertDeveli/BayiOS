namespace BayiOS.Modules.Warehouse.Domain;

/// <summary>What one re-allocation did to a task's lines; the caller persists the added lines.</summary>
/// <param name="Emptied">Source lines left with nothing to pick (<c>qty_to_pick = 0</c>): kept, never deleted, hidden in responses.</param>
internal sealed record LineReallocation(decimal Moved, IReadOnlyList<PickingTaskLine> Added, IReadOnlyList<PickingTaskLine> Emptied)
{
    public static readonly LineReallocation None = new(0m, [], []);
}

/// <summary>
/// Pure rule of #98: when an order line's reservation moves from one shelf to another, the <b>open</b> quantity of the
/// active pick task follows it. Picked and reported-short quantities never move (the goods are in the picker's hands or
/// missing). Per (order line, shelf) a task keeps at most one line, so a shipment never gets two lines for the same
/// (order line, location):
/// <list type="bullet">
/// <item>an untouched line moving as a whole to a shelf the order line has no task line on just changes its shelf;</item>
/// <item>otherwise the source line gives up quantity and the target line grows (an earlier emptied line on that shelf
/// is reused), or is created with the next sequence number. A source left with nothing to pick stays as an empty
/// line (<c>qty_to_pick = 0</c>): task lines are never deleted (the application role has no DELETE on them).</item>
/// </list>
/// Quantity beyond the task's open quantity on the source shelf (reserved but not on this task) is ignored.
/// </summary>
internal static class PickingReallocation
{
    public static LineReallocation Apply(PickingTask task, Guid orderLineId, Guid fromLocationId, Guid toLocationId, decimal quantity)
    {
        if (task.Type != PickingTaskTypes.Pick || !PickingTaskStatuses.IsActive(task.Status) || quantity <= 0 || fromLocationId == toLocationId)
        {
            return LineReallocation.None;
        }

        var sources = task.Lines
            .Where(l => l.OrderLineId == orderLineId && l.LocationId == fromLocationId && l.Remaining > 0)
            .OrderBy(l => l.Sequence)
            .ToList();
        var target = task.Lines.FirstOrDefault(l => l.OrderLineId == orderLineId && l.LocationId == toLocationId);
        var added = new List<PickingTaskLine>();
        var emptied = new List<PickingTaskLine>();
        var left = quantity;
        foreach (var source in sources)
        {
            var take = Math.Min(left, source.Remaining);
            if (take <= 0)
            {
                break;
            }

            if (target is null && take == source.QtyToPick)
            {
                source.MoveTo(toLocationId);
                target = source;
            }
            else
            {
                if (target is null)
                {
                    target = PickingTaskLine.Create(
                        task, task.Lines.Max(l => l.Sequence) + 1, orderLineId, source.VariantId, source.Sku, source.Name, source.Unit, toLocationId, take);
                    task.Lines.Add(target);
                    added.Add(target);
                }
                else
                {
                    target.ChangeQtyToPick(take);
                }

                source.ChangeQtyToPick(-take);
                if (source.QtyToPick == 0)
                {
                    emptied.Add(source);
                }
            }

            left -= take;
        }

        return new LineReallocation(quantity - left, added, emptied);
    }
}
