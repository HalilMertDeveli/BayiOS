using BayiOS.Modules.Warehouse.Domain;

namespace BayiOS.UnitTests.Warehouse;

/// <summary>The full picking-task and shipment transition tables (FulfillmentStateMachine is the single place).</summary>
public class FulfillmentStateMachineTests
{
    private static readonly string[] TaskActions =
    [
        FulfillmentActions.Assign, FulfillmentActions.Claim, FulfillmentActions.Start, FulfillmentActions.Pick,
        FulfillmentActions.ReportShortage, FulfillmentActions.Complete, FulfillmentActions.Cancel,
    ];

    private static readonly string[] ShipmentActions =
    [
        FulfillmentActions.Pack, FulfillmentActions.ReportShortage, FulfillmentActions.Confirm, FulfillmentActions.Cancel,
    ];

    private static readonly (string Action, string Type, string From, bool Shortage, string To)[] LegalTask =
    [
        (FulfillmentActions.Assign, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.Open),
        (FulfillmentActions.Assign, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Assign, PickingTaskTypes.Putaway, PickingTaskStatuses.Open, false, PickingTaskStatuses.Open),
        (FulfillmentActions.Assign, PickingTaskTypes.Putaway, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Claim, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.Open),
        (FulfillmentActions.Claim, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Claim, PickingTaskTypes.Putaway, PickingTaskStatuses.Open, false, PickingTaskStatuses.Open),
        (FulfillmentActions.Claim, PickingTaskTypes.Putaway, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Start, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Start, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Start, PickingTaskTypes.Putaway, PickingTaskStatuses.Open, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Start, PickingTaskTypes.Putaway, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Pick, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Pick, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.ReportShortage, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.ReportShortage, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.InProgress),
        (FulfillmentActions.Complete, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.Completed),
        (FulfillmentActions.Complete, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.Completed),
        (FulfillmentActions.Complete, PickingTaskTypes.Pick, PickingTaskStatuses.Open, true, PickingTaskStatuses.CompletedWithShortage),
        (FulfillmentActions.Complete, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, true, PickingTaskStatuses.CompletedWithShortage),
        (FulfillmentActions.Complete, PickingTaskTypes.Putaway, PickingTaskStatuses.Open, false, PickingTaskStatuses.Completed),
        (FulfillmentActions.Complete, PickingTaskTypes.Putaway, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.Completed),
        (FulfillmentActions.Complete, PickingTaskTypes.Putaway, PickingTaskStatuses.Open, true, PickingTaskStatuses.Completed),
        (FulfillmentActions.Complete, PickingTaskTypes.Putaway, PickingTaskStatuses.InProgress, true, PickingTaskStatuses.Completed),
        (FulfillmentActions.Cancel, PickingTaskTypes.Pick, PickingTaskStatuses.Open, false, PickingTaskStatuses.Cancelled),
        (FulfillmentActions.Cancel, PickingTaskTypes.Pick, PickingTaskStatuses.InProgress, false, PickingTaskStatuses.Cancelled),
    ];

    private static readonly (string Action, string From, string To)[] LegalShipment =
    [
        (FulfillmentActions.Pack, ShipmentStatuses.Draft, ShipmentStatuses.Packed),
        (FulfillmentActions.ReportShortage, ShipmentStatuses.Draft, ShipmentStatuses.Draft),
        (FulfillmentActions.Confirm, ShipmentStatuses.Draft, ShipmentStatuses.Confirmed),
        (FulfillmentActions.Confirm, ShipmentStatuses.Packed, ShipmentStatuses.Confirmed),
        (FulfillmentActions.Cancel, ShipmentStatuses.Draft, ShipmentStatuses.Cancelled),
        (FulfillmentActions.Cancel, ShipmentStatuses.Packed, ShipmentStatuses.Cancelled),
    ];

    [Fact]
    public void Exactly_the_documented_task_transitions_are_legal()
    {
        foreach (var action in TaskActions)
        {
            foreach (var type in PickingTaskTypes.All)
            {
                foreach (var status in PickingTaskStatuses.All)
                {
                    foreach (var shortage in new[] { false, true })
                    {
                        // Only "complete" depends on the shortage flag; the other rows are listed once.
                        var expected = LegalTask.FirstOrDefault(l => l.Action == action && l.Type == type && l.From == status
                            && (action != FulfillmentActions.Complete || l.Shortage == shortage)).To;
                        var actual = FulfillmentStateMachine.NextTask(action, type, status, shortage);
                        Assert.True(expected == actual, $"{action} {type} from {status} (shortage {shortage}): expected {expected ?? "illegal"}, got {actual ?? "illegal"}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Exactly_the_documented_shipment_transitions_are_legal()
    {
        foreach (var action in ShipmentActions)
        {
            foreach (var status in ShipmentStatuses.All)
            {
                var expected = LegalShipment.FirstOrDefault(l => l.Action == action && l.From == status).To;
                var actual = FulfillmentStateMachine.NextShipment(action, status);
                Assert.True(expected == actual, $"{action} from {status}: expected {expected ?? "illegal"}, got {actual ?? "illegal"}");
            }
        }
    }

    [Fact]
    public void Terminal_tasks_and_shipments_never_change()
    {
        foreach (var action in TaskActions)
        {
            foreach (var type in PickingTaskTypes.All)
            {
                foreach (var terminal in new[] { PickingTaskStatuses.Completed, PickingTaskStatuses.CompletedWithShortage, PickingTaskStatuses.Cancelled })
                {
                    Assert.Null(FulfillmentStateMachine.NextTask(action, type, terminal));
                }
            }
        }

        foreach (var action in ShipmentActions)
        {
            Assert.Null(FulfillmentStateMachine.NextShipment(action, ShipmentStatuses.Confirmed));
            Assert.Null(FulfillmentStateMachine.NextShipment(action, ShipmentStatuses.Cancelled));
        }

        // Putaway tasks are never scanned and never cancelled.
        Assert.Null(FulfillmentStateMachine.NextTask(FulfillmentActions.Pick, PickingTaskTypes.Putaway, PickingTaskStatuses.Open));
        Assert.Null(FulfillmentStateMachine.NextTask(FulfillmentActions.Cancel, PickingTaskTypes.Putaway, PickingTaskStatuses.Open));
    }
}
