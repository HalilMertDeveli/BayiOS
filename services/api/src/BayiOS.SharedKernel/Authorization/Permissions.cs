namespace BayiOS.SharedKernel.Authorization;

/// <summary>
/// Canonical permission ids, named exactly as in docs/product/roles-permissions.md §3.
/// </summary>
public static class Permissions
{
    // §3.1 Tenant, members, subscription
    public const string TenantRead = "tenant.read";
    public const string TenantSettingsManage = "tenant.settings.manage";
    public const string TenantDeleteRequest = "tenant.delete.request";
    public const string MembersRead = "members.read";
    public const string MembersInvite = "members.invite";
    public const string MembersManage = "members.manage";
    public const string OwnershipTransfer = "ownership.transfer";
    public const string BillingRead = "billing.read";
    public const string BillingManage = "billing.manage";
    public const string AuditRead = "audit.read";
    public const string SecurityManage = "security.manage";

    // §3.2 Catalog and prices
    public const string CatalogRead = "catalog.read";
    public const string CatalogWrite = "catalog.write";
    public const string CatalogBarcodeAssign = "catalog.barcode.assign";
    public const string CatalogFieldsManage = "catalog.fields.manage";
    public const string PricesReadSell = "prices.read.sell";
    public const string PricesReadCost = "prices.read.cost";
    public const string PricesWrite = "prices.write";
    public const string PricelistsAssign = "pricelists.assign";

    // §3.3 Imports, exports, reports
    public const string ImportsRun = "imports.run";
    public const string ImportsRunStock = "imports.run.stock";
    public const string ImportsRollback = "imports.rollback";
    public const string ReportsRead = "reports.read";
    public const string ReportsExport = "reports.export";
    public const string DataExportFull = "data.export.full";

    // §3.4 Inventory and warehouse
    public const string InventoryRead = "inventory.read";
    public const string InventoryMovementsRead = "inventory.movements.read";
    public const string InventoryReceive = "inventory.receive";
    public const string InventoryReceiveApprove = "inventory.receive.approve";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryCountManage = "inventory.count.manage";
    public const string InventoryCountPerform = "inventory.count.perform";
    public const string InventoryTransfer = "inventory.transfer";
    public const string InventoryTransferReceive = "inventory.transfer.receive";
    public const string InventoryReturns = "inventory.returns";
    public const string WarehousesManage = "warehouses.manage";

    // §3.5 Orders and customers
    public const string OrdersRead = "orders.read";
    public const string OrdersCreate = "orders.create";
    public const string OrdersEditDraft = "orders.edit.draft";
    public const string OrdersSubmit = "orders.submit";
    public const string OrdersApprove = "orders.approve";
    public const string OrdersEditApproved = "orders.edit.approved";
    public const string OrdersPriceOverride = "orders.price.override";
    public const string OrdersCancel = "orders.cancel";
    public const string OrdersShip = "orders.ship";
    public const string WhatsappParse = "whatsapp.parse";
    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string CustomersGroupsManage = "customers.groups.manage";

    // §3.6 Warehouse tasks
    public const string TasksReadAll = "tasks.read.all";
    public const string TasksReadOwn = "tasks.read.own";
    public const string TasksAssign = "tasks.assign";
    public const string TasksClaim = "tasks.claim";
    public const string TasksPick = "tasks.pick";
    public const string TasksPack = "tasks.pack";
    public const string TasksReportShortage = "tasks.report_shortage";
}
