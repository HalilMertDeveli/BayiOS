# Katkı Rehberi

## Akış
1. Her iş bir GitHub Issue ile başlar (kabul kriterleri zorunlu).
2. `preview` dalından branch açın: `feature/<issue>-<kisa-ad>`, `fix/<issue>-<kisa-ad>`, `chore/<issue>-<kisa-ad>`.
3. Küçük, odaklı commit'ler; mesaj formatı: `<tür>(<kapsam>): <özet> (#<issue>)` — tür: feat, fix, chore, docs, test, refactor.
4. PR hedefi `preview`. PR şablonundaki kapsam, test kanıtı, risk ve ekran görüntüsü bölümleri doldurulur.
5. CI yeşil + review onayı olmadan merge yok. `main`'e yalnızca ürün sahibi onayıyla `preview` → `main` PR'ı ile geçilir.

## Yerel geliştirme
```bash
docker compose -f infra/docker/compose.yml up -d
dotnet test services/api/BayiOS.sln
npm --prefix apps/web ci && npm --prefix apps/web run dev
```

## Kurallar
- Gerçek anahtar, connection string, müşteri verisi veya gerçek fiyat listesi commit edilmez.
- Başarısız testler atlanmaz veya gevşetilmez.
- Stok değişiklikleri yalnızca ledger hareketiyle; tenant filtresi her sorguda.
- Ayrıntılar: `CLAUDE.md`, `docs/testing/test-strategy.md`, `docs/security/threat-model.md`.
