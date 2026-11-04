using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.Modules.Warehouse.Contracts;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>HTTP helpers for the picking / packing / shipment API (fresh Idempotency-Key unless one is given).</summary>
internal static class Fulfillment
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The order's active pick task (supervisor view).</summary>
    public static async Task<PickingTaskResponse> ActiveTaskAsync(CatalogClient supervisor, Guid orderId)
    {
        var page = await ReadAsync<PickingTaskPageResponse>(
            await supervisor.GetAsync($"/api/v1/picking-tasks?orderId={orderId}&type=pick&status=open,in_progress"));
        var summary = Assert.Single(page.Items);
        return await TaskAsync(supervisor, summary.Id);
    }

    public static async Task<PickingTaskResponse> TaskAsync(CatalogClient client, Guid taskId) =>
        await ReadAsync<PickingTaskResponse>(await client.GetAsync($"/api/v1/picking-tasks/{taskId}"));

    public static Task<HttpResponseMessage> PostAsync(CatalogClient client, string path, object? body = null, string? key = null, long? ifMatch = null) =>
        client.SendAsync(
            HttpMethod.Post,
            path,
            body ?? new { },
            ifMatch is { } version ? CatalogClient.ETag(version) : null,
            idempotencyKey: key ?? Guid.NewGuid().ToString());

    public static Task<HttpResponseMessage> ScanAsync(
        CatalogClient client, Guid taskId, string code, decimal? quantity = null, string? location = null, Guid? lineId = null, string? key = null) =>
        PostAsync(client, $"/api/v1/picking-tasks/{taskId}/scans", new { code, location, quantity, lineId }, key);

    public static Task<HttpResponseMessage> ShortageAsync(CatalogClient client, Guid taskId, Guid lineId, string reason, decimal? quantity = null) =>
        PostAsync(client, $"/api/v1/picking-tasks/{taskId}/lines/{lineId}/shortage", new { quantity, reason });

    public static Task<HttpResponseMessage> CompleteAsync(CatalogClient client, Guid taskId) =>
        PostAsync(client, $"/api/v1/picking-tasks/{taskId}/complete");

    public static Task<HttpResponseMessage> ClaimAsync(CatalogClient client, Guid taskId) =>
        PostAsync(client, $"/api/v1/picking-tasks/{taskId}/claim");

    public static Task<HttpResponseMessage> AssignAsync(CatalogClient client, Guid taskId, Guid userId) =>
        PostAsync(client, $"/api/v1/picking-tasks/{taskId}/assign", new { userId });

    public static Task<HttpResponseMessage> ConfirmAsync(CatalogClient client, Guid shipmentId, string? key = null, string? carrier = "Sentetik Kargo") =>
        PostAsync(client, $"/api/v1/shipments/{shipmentId}/confirm", new { carrier, trackingNumber = "TRK-0001" }, key);

    public static Task<HttpResponseMessage> PackAsync(CatalogClient client, Guid shipmentId, params object[][] packages) =>
        PostAsync(client, $"/api/v1/shipments/{shipmentId}/pack", new { packages = packages.Select(items => new { items }) });

    public static object Item(string code, decimal quantity) => new { code, quantity };

    /// <summary>
    /// Picks <paramref name="picks"/> (per order line) on the task — scanning the SKU at the line's location — reports
    /// whatever stays open as an "insufficient" shortage, and completes the task. Returns the completed task.
    /// </summary>
    public static async Task<PickingTaskResponse> PickAndCompleteAsync(CatalogClient client, PickingTaskResponse task, IReadOnlyDictionary<Guid, decimal> picks)
    {
        var left = picks.ToDictionary(p => p.Key, p => p.Value);
        foreach (var line in task.Lines.OrderBy(l => l.Sequence))
        {
            var take = Math.Min(line.QuantityRemaining, left.GetValueOrDefault(line.OrderLineId));
            if (take > 0)
            {
                await ReadAsync<PickScanResponse>(await ScanAsync(client, task.Task.Id, line.Sku, take, line.LocationCode, line.Id));
                left[line.OrderLineId] -= take;
            }

            if (line.QuantityRemaining - take > 0)
            {
                await ReadAsync<PickingTaskResponse>(await ShortageAsync(client, task.Task.Id, line.Id, "insufficient", line.QuantityRemaining - take));
            }
        }

        return await ReadAsync<PickingTaskResponse>(await CompleteAsync(client, task.Task.Id));
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
