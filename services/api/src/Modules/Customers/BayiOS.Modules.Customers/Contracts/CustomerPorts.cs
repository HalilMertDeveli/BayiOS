namespace BayiOS.Modules.Customers.Contracts;

/// <summary>
/// Port: personal data about a customer that other modules keep (ADR 0005 — no cross-module FKs or references).
/// Implemented by the host over the Orders module's <c>IOrderPersonalData</c>; runs on the request UnitOfWork
/// connection, inside the anonymization transaction.
/// </summary>
public interface ICustomerOrderPersonalData
{
    /// <summary>
    /// Masks the personal fields of the customer's order address snapshots (#115). Idempotent; returns how many
    /// orders this call changed.
    /// </summary>
    Task<int> AnonymizeOrderSnapshotsAsync(Guid customerId, CancellationToken cancellationToken);
}
