import { CustomerEditor } from "@/components/customers/customer-editor";

export const metadata = { title: "Yeni müşteri" };

export default function YeniMusteriPage() {
  return <CustomerEditor customerId={null} />;
}
