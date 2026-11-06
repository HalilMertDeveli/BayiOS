# 14. KVKK: müşteri anonimleştirme

## Amaç
Bir kişi KVKK kapsamında kişisel verilerinin silinmesini istediğinde, o müşterinin kişisel bilgilerini kalıcı olarak silmek;
sipariş geçmişini (muhasebe ve stok doğruluğu için) korumak.

Kimler: yalnızca **işletme sahibi** ve **yönetici**.

**Bu işlem geri alınamaz.**

## Ne silinir, ne kalır?
| Silinir | Kalır |
|---|---|
| Ad, yetkili, vergi no, telefon, e-posta, WhatsApp numaraları ve notlar | Müşteri kodu (ör. C-001) |
| Tüm adresler | Sipariş geçmişi |
| – | Müşteri arşivlenir ve bir daha değiştirilemez |

## Adımlar
1. **Müşteriler** listesinden müşteriyi açın.
2. Sayfanın altındaki **KVKK: kişisel verileri sil** bölümünde **Müşteriyi anonimleştir** düğmesine basın.
3. "Müşteri anonimleştirilsin mi?" penceresinde silinecekleri okuyun.
4. **Onaylamak için müşteri adını yazın** kutusuna müşterinin adını **tam olarak** yazın. Ad eşleşince düğme etkinleşir.
5. **Kalıcı olarak anonimleştir** düğmesine basın.
6. "Müşteri anonimleştirildi — Kod … sipariş geçmişi için korunuyor." bildirimi çıkar.

## Sık hatalar ve mesajlar
| Mesaj | Anlamı |
|---|---|
| Düğme etkin değil | Yazdığınız ad müşteri adıyla tam eşleşmiyor. |
| Bu müşteri anonimleştirilmiş; kayıt artık değiştirilemez. | İşlem daha önce yapılmış. |
| Bu işlem için yetkiniz yok. … | Yalnızca sahip ve yönetici yapabilir. |

## İpuçları
- Talebi aldığınız tarihi ve talep sahibini kendi kayıtlarınızda tutun; BayiOS bu işlemi denetim kaydına yazar.
- Anonimleştirmeden önce müşterinin açık siparişlerini kontrol edin; anonimleştirilen müşteri yeni siparişte seçilemez (arşivlenir).
- Bu işlem yalnızca müşteri kaydı içindir; çalışan (kullanıcı) hesabı silme talepleri için BayiOS ekibine başvurun.
