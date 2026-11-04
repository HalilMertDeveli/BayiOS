# AUTOPILOT — Otonom yürütme ve ajan koordinasyonu

## 1. Delegasyon yöntemi (gerçekte çalışan)
- **Agent Teams:** Bu ortamda etkin değil (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS` tanımlı değil). Kullanılmıyor.
- **Kullanılan yöntem:** Claude Code alt ajanları (Agent aracı). Rol tanımları `.claude/agents/*.md` dosyalarındadır.
  Kod yazan paralel ajanlar `isolation: worktree` ile ayrı git worktree'de çalışır.
- Alt ajanlar kullanıcıyla konuşmaz; sonuçlarını Orkestra Şefi'ne raporlar. Şef doğrular, birleştirir, raporlar.
- Ajan belleği kalıcı değildir. Süreklilik GitHub Issues/PR'lar + `STATUS.md` + bu belgelerle sağlanır.

## 2. Görev sözleşmesi (her ajan görevlendirmesinde zorunlu)
```
Görev ID     : #<issue>
Rol          : <.claude/agents/ dosya adı>
Kapsam       : <ne yapılacak / ne yapılmayacak>
Girdiler     : <okunacak belgeler, API sözleşmesi>
Çıktı        : <dosyalar, testler, PR>
Kabul kriteri: <issue'daki maddeler>
Branch       : feature|fix|chore/<issue>-<ad>
Dokunabilir  : <dizin/glob listesi>  — dışına çıkma
Bağımlılık   : <önce bitmesi gereken issue'lar>
Test         : <çalıştırılacak komutlar, beklenen sonuç>
```
Ajan raporu: değişen dosyalar, çalıştırılan komutlar ve gerçek çıktıları, açık riskler.

## 3. Paralellik
- Aynı anda en fazla 3–5 bağımsız iş akışı. Aynı dosya kümesine iki yazar yok.
- Sözleşme önce: API değişikliği → `packages/contracts` (OpenAPI) güncellenir, sonra web/mobil.
- Bağımlı işler sıralı yürür. Paralellik maliyeti haklı çıkarmıyorsa sıralı çalışılır.

## 4. Onay sınırları
**Ürün sahibi onayı olmadan YAPILMAZ:** production deploy, production migration, `main` merge, ücretli plan/kaynak
(yeni Supabase projesi dahil — maliyet önce raporlanır), domain satın alma, canlı DNS, canlı ödeme, gerçek WhatsApp
mesajı gönderimi, gerçek müşteri verisi silme/aktarma, gizli anahtar paylaşımı, güvenlik korumasını kapatma.

**Bağımsız yürütülür:** araştırma, issue/milestone, branch, kod, test, refactor, doküman, PR (`preview` hedefli),
sentetik demo verisi, ücretsiz preview yapılandırması.

## 5. Durdurma koşulları
Tenant izolasyon hatası, stok tutarsızlığı, kritik güvenlik testi başarısızlığı, yanlış ortamda migration,
yetkisiz maliyet, `main` koruması ihlali → ilgili iş `BLOCKED` etiketi alır, `STATUS.md`'ye yazılır, diğer işler sürer.

## 6. Günlük döngü
Başlangıç: issues/PR/CI oku → blokerleri belirle → kritik yolu güncelle → görev dağıt.
Gün içi: PR incele, testleri doğrula, çakışmaları çöz.
Bitiş: `STATUS.md` güncelle (gerçek PR/commit/test kanıtıyla), sonraki işi hazırla.

## 7. Raporlama
Kullanıcıya Türkçe "BAYIOS — CHIEF ORCHESTRATOR REPORT" formatı. Yalnızca gerçekleşmiş işler; uydurma link/test yok.
