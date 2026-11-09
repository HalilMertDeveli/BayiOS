# infra/helm — Kubernetes chart'ı

`bayios/`: API + web Deployment/Service, ConfigMap/Secret, Ingress, probe'lar, opsiyonel HPA; kind/dev için küme içi
PostgreSQL ve DB bootstrap + EF migration Job'ı (`values-kind.yaml`). Sır yok: gerçek ortamda bağlantı cümlesi
dışarıda oluşturulan bir Secret'tan okunur.

Nasıl çalıştırılır, neyin sahte olduğu, CI (`.github/workflows/k8s-kind.yml`):
[`docs/operations/kubernetes.md`](../../docs/operations/kubernetes.md).
