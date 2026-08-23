<p align="center">
  <img src="docs/img/banner.png" alt="wymcmd - CMD'm Neden Açıldı" width="900">
</p>

<p align="center">
  <b>Ekranda bir konsol penceresi parladı ve kayboldu. Onu neyin açtığını burada görürsün.</b><br>
  <a href="README.md">English</a> · Windows 10/11 · .NET 10 · tek dosya
</p>

---

Sen bakana kadar Görev Yöneticisi boşalmış oluyor, Process Monitor ise yalnızca bir şeyin
çalıştığını söylüyor, *neden* çalıştığını değil. wymcmd asıl soruyu cevaplıyor: **o konsolu hangi
zamanlanmış görev, hangi Run anahtarı, hangi servis, hangi belge ya da hangi tıklama açtı** - ve
bunu wymcmd hiç çalışmıyorken olmuş açılışlar için de cevaplıyor.

```
> wymcmd why last --lang tr

cmd.exe  (pid 24188)
\Microsoft\Windows\UpdateOrchestrator\Reboot zamanlanmış görevi çalıştırdı → svchost.exe → cmd.exe

başlangıç      23 Ağustos 2026 Pazar 03:11:04  (7 saat önce)
ömür           42 ms
dosya          C:\Windows\System32\cmd.exe
komut          cmd.exe /c shutdown /r /f /t 0
imza           Microsoft Windows tarafından imzalı
pencere        gizli / pencere yok
başlatan       Zamanlanmış Görev: \Microsoft\Windows\UpdateOrchestrator\Reboot
güven          kesin
kanıt          SecurityLog, TaskLog

risk: 25/100
  +25  görünür pencere yok
```

## Arka planda hiçbir şey çalışmıyor

Bu eksik bir özellik değil, bilinçli bir karar. wymcmd'nin olan biteni öğrenmek için dört yolu
var; yalnızca sonuncusu yerleşik bir süreç ve o da **kapalı geliyor**.

| Mod | Yerleşik süreç | Ne veriyor |
|---|---|---|
| **Adli** (varsayılan) | yok | Windows'un zaten tuttuğu kayıtlardan geçmişi geri kurar: Güvenlik günlüğü 4688, Sysmon, Görev Zamanlayıcı, PowerShell script blokları, Prefetch, BAM, AmCache |
| **Kara kutu** (önerilen) | **yok** | Açılışta **Windows'un kendisinin** başlattığı bir ETW AutoLogger, tavanı belli dairesel dosyaya yazar. Boştayken CPU maliyeti sıfır, bellekte bize ait hiçbir şey yok, sonra aracı açtığında tam kayıt hazır |
| **Canlı** | yalnızca açıkken | `wymcmd watch` ya da pencere açıkken gerçek zamanlı çekirdek izleme |
| **Tuzak** | süresi dolana kadar | "Bir daha olursa yakala", süreli; süre bitince kendini kapatır |
| **Nöbet servisi** | evet, isteğe bağlı | 7/24 kural uygulaması isteyenler için |

```
wymcmd doctor           # bu makine şu an neyi söyleyebiliyor
wymcmd sources enable   # Windows süreç oluşturmayı komut satırıyla kaydetsin
wymcmd blackbox on      # açılış kaydı, yine yerleşik süreç yok
```

## Gerçekte ne çıkarıyor

- **Kim başlattı** - kök sürece kadar tüm üst zincir, çoktan kapanmış üstler dahil
- **Neden başladı** - Zamanlanmış Görev, Run anahtarı, Başlangıç klasörü, servis, WMI aboneliği,
  Image File Execution Options, kurulum programı, Office belgesi, tarayıcı indirmesi ya da senin
  çift tıklaman
- **Ne çalıştırdı** - `-EncodedCommand` çözülüp gerçek script gösterilir, `cmd /c` sarması açılır,
  PowerShell kaydından gerçek script bloğu getirilir
- **Penceresi var mıydı** - penceresiz bir konsol, bir şeyin görünmek istemediğinin en güçlü
  işaretidir (katalog imzalı Windows dosyaları doğru tanınır, sistem araçları imzasız diye
  işaretlenmez)
- **Ne kadar endişelenmeli** - 0-100 arası skor, ama her zaman gerekçeleri listelenmiş hâlde

## Komutlar

```
wymcmd                          # pencere
wymcmd why <pid|last>           # tek bir açılışı açıkla, gerekirse geçmişe dönük
wymcmd timeline 14:22           # o anın etrafındaki her şey
wymcmd list --last 24h --hidden --unsigned --risk 50
wymcmd watch --console          # bu pencere açık kaldığı sürece canlı akış
wymcmd trap --image cmd.exe --hidden-only --for 2h --action killtree
wymcmd tree [pid]
wymcmd kill <pid> [--tree]
wymcmd rules add --image cmd.exe --match "downloadstring" --action kill
wymcmd rules test               # kuralların son 24 saatte ne yapmış olacağı
wymcmd export --since 24h --format csv|jsonl|report
wymcmd blackbox on|off|status
wymcmd sources enable|status
wymcmd service install|start|stop|uninstall
wymcmd uninstall --purge        # her değişikliği geri al, her dosyayı sil
```

Her komut `--json` (makine-okunur, anahtarlar hep İngilizce) ve `--lang en|tr` destekler.

## Kurallar

Kurallar yalnızca canlı, tuzak ve nöbet modunda çalışır - izlemek tek başına makineni değiştirmez.

```
wymcmd rules add --image powershell.exe --hidden --unsigned --action killtree --name "gizli imzasız kabuk"
```

Eşleme alanları: dosya adı, yol, komut satırı regex'i, üst süreç, zincirdeki herhangi bir ata,
imzalayan, kullanıcı, oturum, pencere durumu, bütünlük seviyesi, temp yolu ve risk skoru.
Aksiyonlar: `log`, `notify`, `hide`, `suspend`, `kill`, `killtree` ve beyaz liste için `allow`.
Kural eklendiği anda, kayıtlı geçmişte kaç kez eşleşeceği gösterilir - kör kural kurulmaz.

`csrss.exe`, `lsass.exe`, `services.exe`, `winlogon.exe` ve benzerlerini hiçbir bayrakla
atlatılamayan bir koruma reddeder.

## Kurulum

Derlemek için [.NET 10 SDK](https://dotnet.microsoft.com/download) gerekir.

```
git clone https://github.com/talkdedsec/wymcmd
cd wymcmd
dotnet publish src/Wymcmd/Wymcmd.csproj -c Release
```

Sonuç tek dosyalık, kendi kendine yeten bir `wymcmd.exe`. `PATH` üzerinde herhangi bir yere koy.

## Gizlilik

Her şey makinende kalır: `%ProgramData%\wymcmd` (yönetici değilse `%LOCALAPPDATA%\wymcmd`)
altında bir SQLite veritabanı. Telemetri yok, ağ çağrısı yok - isteğe bağlı hash sorgusu
varsayılan olarak kapalıdır ve kendiliğinden açılmaz. `wymcmd uninstall --purge` verileri, kara
kutu kaydını, servisi ve wymcmd'nin yaptığı tüm denetim politikası değişikliklerini geri alır.

## Dil

Kaynak dil İngilizce, Türkçe tam çeviri - arayüz, CLI çıktısı, yardım metni, hata mesajları ve
raporlar dahil. `--lang tr` ya da penceredeki dil seçici.

## Lisans

Kaynağı açık, **açık kaynak değil**: kullanımı serbest, **değiştirme yok, yeniden dağıtma yok,
satma yok**. Bkz. [LICENSE](LICENSE).
