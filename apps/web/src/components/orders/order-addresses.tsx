import { addressLines, isMaskedAddress, type OrderAddress } from "@/lib/orders/addresses";

/**
 * Shipping and billing address of an order (#92). Drafts show a live preview of the chosen (else default) address;
 * from submit on the API returns the snapshot copied onto the order, which later customer edits do not change.
 * Only readers with customers.read get addresses from the API (the caller hides the section otherwise).
 */
export function OrderAddresses({
  shipping,
  billing,
  snapshot,
}: {
  shipping: OrderAddress | null;
  billing: OrderAddress | null;
  snapshot: boolean;
}) {
  return (
    <section aria-labelledby="order-addresses" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
      <div className="flex flex-col gap-0.5">
        <h2 id="order-addresses" className="text-h3 text-fg">
          Adresler
        </h2>
        <p className="text-caption text-fg-muted">
          {snapshot
            ? "Sipariş gönderildiğinde kaydedilen adresler; müşteri kartındaki sonraki değişiklikler bu siparişi etkilemez."
            : "Önizleme: sipariş onaya gönderildiğinde bu adresler siparişe kopyalanır."}
        </p>
      </div>
      <dl className="grid grid-cols-1 gap-4 text-body sm:grid-cols-2">
        <AddressBlock label="Teslimat adresi" address={shipping} snapshot={snapshot} />
        <AddressBlock label="Fatura adresi" address={billing} snapshot={snapshot} />
      </dl>
    </section>
  );
}

function AddressBlock({ label, address, snapshot }: { label: string; address: OrderAddress | null; snapshot: boolean }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-caption text-fg-muted">{label}</dt>
      <dd className="text-fg">
        {address ? (
          <address className="not-italic">
            {addressLines(address).map((line, i) => (
              <span key={i} className={i === 0 && address.title ? "block font-medium" : "block"}>
                {line}
              </span>
            ))}
            {isMaskedAddress(address) ? (
              <span className="mt-1 block text-caption text-fg-muted">Müşterinin kişisel verileri silindiği (KVKK) için adres maskelendi; yalnızca ilçe, il ve ülke saklanır.</span>
            ) : null}
          </address>
        ) : (
          <span className="text-fg-muted">{snapshot ? "Siparişte kayıtlı adres yok." : "Adres yok: seçili adres bulunamadı veya müşterinin bu türde varsayılan adresi yok."}</span>
        )}
      </dd>
    </div>
  );
}
