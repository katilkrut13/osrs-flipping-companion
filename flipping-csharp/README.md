# C#/.NET sürümü

Masaüstündeki `Flipping-Bot.exe` artık self-contained C#/.NET 10 sürümüdür. Python kurulumu veya Python çalışma zamanı istemez. RuneLite Java köprüsü korunur; C# uygulaması `RuneLite/config.json` içindeki `flip.directory` değerini okuyarak mevcut `config.json`, SQLite veritabanı, pozisyon günlüğü, tahsilat makbuzları ve banka görevlerini aynı klasörden kullanır.

Kullanım: RuneLite/Jagex Launcher ile hesabı açıp GE teklif ekranını hazırlayın, masaüstündeki EXE’yi açın, bağlantı göründükten sonra `BAŞLAT` düğmesine basın. `DURDUR`, yeni girişleri keser ve açık bot teklifleri için mevcut temizleme/tahsilat akışını çalıştırır. İlk canlı bağlantıda alış geçmişi doğrulaması gerektiğinde Limit ve kayıt sekmesinden doğru beyanı seçin.

Karar motoru artık vergi sonrası marjı; 5m/1h/24h hacim şekli, iki taraflı likidite, geçmiş kapsamı, oynaklık, beklenen dolum oranı, gerçekleşen çevrim süresi, test maliyeti ve sermaye hızıyla birlikte değerlendirir. Falling-knife/dump filtreleri sert blok olarak kalır; eski piyasa raporu veya düşük beklenen stres kârı yeni alımı engeller. Her adayın kararı `decision-diagnostics.json` içinde nedenleri, beklenen kârı, kalite skoru ve güven derecesiyle görünür.

Kontroller çevrimdışı yapıldı: 125 native C# kontrolü ve simülasyon GUI testi geçti. Dönüşüm sırasında canlı oyun girdisi gönderilmedi. RuneLite snapshot’ı güncel değilse C# sürümü emir göndermez ve bağlantı bekler. Ayrıntılı kanıt `CSHARP-MIGRATION-VALIDATION.json` dosyasındadır.

Önceki Python EXE’si geri dönüş için `work/python-exe-backup-before-csharp-20261009.exe` altında tutulur.
