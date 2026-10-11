# OSRS Flipping Companion

Windows üzerinde çalışan C#/.NET Grand Exchange piyasa analiz ve işlem uygulaması. OSRS Wiki gerçek zamanlı fiyat verilerini kullanır; RuneLite için yerel Java köprüsü üzerinden teklifleri ve tahsilatları yönetir.

## Özellikler

- GE vergisi sonrası net marj hesabı
- 5 dakika, 1 saat ve 24 saat hacim analizi
- düşen fiyat, olağandışı hacim ve likidite filtreleri
- gerçekleşen dolum, çevrim süresi ve kâr geçmişine göre sıralama
- açık teklif, kısmi dolum, Collect ve yeniden başlatma uzlaştırması
- tam eşleşen stok için otomatik kurtarma
- sekiz GE slotuna kadar dinamik sermaye dağıtımı
- hesap bazlı işlem ve kâr günlüğü

## Derleme

Gereksinimler:

- Windows x64
- .NET 10 SDK
- JDK 17 ve Gradle (RuneLite köprüsünü yeniden derlemek için)

Masaüstü uygulamasını yayımlama:

```powershell
dotnet publish flipping-csharp/Flipping.Desktop/Flipping.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Çevrimdışı kontroller:

```powershell
dotnet run --project flipping-csharp/Flipping.Desktop/Flipping.Desktop.csproj -c Release -- --self-test self-test.json
```

## Veri güvenliği

Çalışma zamanı dosyaları `%LOCALAPPDATA%\OsrsFlipping` altında oluşturulur. Hesap kimliği, işlem geçmişi, SQLite veritabanı ve DPAPI ile korunan banka PIN dosyası depoya eklenmez.

## Uyarı

Bu proje bağımsızdır; Jagex veya RuneLite tarafından onaylanmamıştır. Oyun otomasyonu hesap yaptırımı riski taşıyabilir. Kullanıcı kendi hesabından ve kullanımından sorumludur. Kâr garantisi yoktur.
