<p align="center">
  <img src="docs/img/banner.png" alt="Why My CMD Opened" width="920">
</p>

<h1 align="center">Why My CMD Opened</h1>

<p align="center">
  <b>Ekranda bir konsol penceresi parladı ve kayboldu. Onu neyin açtığını burada görürsün.</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0d1117?style=flat-square&labelColor=0d1117&color=14e39a" alt="Windows 10 ve 11">
  <img src="https://img.shields.io/badge/.NET-10-0d1117?style=flat-square&labelColor=0d1117&color=14e39a" alt=".NET 10">
  <img src="https://img.shields.io/badge/yerle%C5%9Fik%20s%C3%BCre%C3%A7-0-0d1117?style=flat-square&labelColor=0d1117&color=14e39a" alt="Yerlesik surec yok">
  <img src="https://img.shields.io/badge/dil-EN%20%C2%B7%20TR-0d1117?style=flat-square&labelColor=0d1117&color=22d3ee" alt="Ingilizce ve Turkce">
  <img src="https://img.shields.io/badge/lisans-kayna%C4%9F%C4%B1%20a%C3%A7%C4%B1k-0d1117?style=flat-square&labelColor=0d1117&color=f2c14e" alt="Kaynagi acik">
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="#h%C4%B1zl%C4%B1-ba%C5%9Flang%C4%B1%C3%A7">Hızlı başlangıç</a> ·
  <a href="#komutlar">Komutlar</a> ·
  <a href="#nas%C4%B1l-biliyor">Nasıl biliyor</a> ·
  <a href="#gizlilik">Gizlilik</a>
</p>

---

Sen bakana kadar Görev Yöneticisi çoktan boşalmış oluyor. Process Monitor bir şeyin çalıştığını
söylüyor, *neden* çalıştığını değil. **wymcmd** — yazdığın komut — asıl merak ettiğini
cevaplıyor: **o konsolu hangi zamanlanmış görev, hangi Run anahtarı, hangi servis, hangi belge
ya da hangi tıklama açtı** — üstelik wymcmd hiç çalışmıyorken olmuş açılışlar için de.

```console
> wymcmd why last --lang tr

cmd.exe  (pid 24188)
\Microsoft\Windows\UpdateOrchestrator\Reboot zamanlanmış görevi çalıştırdı → svchost.exe → cmd.exe

başlangıç      24 Ağustos 2026 Pazartesi 03:11:04  (7 saat önce)
ömür           42 ms
dosya          C:\Windows\System32\cmd.exe
komut          cmd.exe /c shutdown /r /f /t 0
imza           Microsoft Windows tarafından imzalı
pencere        gizli / pencere yok
başlatan       Zamanlanmış Görev: \Microsoft\Windows\UpdateOrchestrator\Reboot
güven          kesin
kanıt          BlackBox, SecurityLog, TaskLog

çalışma geçmişi
  Prefetch     24.08.2026 03:11  (7 saat önce)
  UserAssist   21.08.2026 19:40  (3 gün önce)  12 kez çalışmış

risk: 25/100
  +25  görünür pencere yok
```

<p align="center">
  <img src="docs/img/gui-tr.png" alt="wymcmd penceresi: canlı açılışlar, üst zincir, çözülmüş komut satırı, risk" width="920">
</p>

Pencere aynı motorun başka bir yüzü: **Zaman tüneli** bir anı tüm kaynaklardan geri kurar,
**Kurallar** her kuralın kaç kez eşleşeceğini gösterir ve seçili açılıştan yeni kural yazar,
**İstatistik** geçmişteki desenleri okur, **Kaynaklar** Windows kaydını açıp kapatır, **Dışa
aktar** ise baktığın listeyi CSV, JSON satırları ya da rapor olarak yazar.

## Arka planda hiçbir şey çalışmıyor

Bu eksik bir özellik değil, bilinçli bir karar. wymcmd'nin olan biteni öğrenmek için beş yolu
var; yalnızca sonuncusu yerleşik bir süreç ve o da **kapalı geliyor**.

| Mod | Yerleşik süreç | Ne veriyor |
|---|:---:|---|
| **Adli** — varsayılan | yok | Windows'un zaten tuttuğu kayıtlardan geçmişi geri kurar: Güvenlik günlüğü 4688/4689, Sysmon, Görev Zamanlayıcı, PowerShell script blokları, Prefetch, BAM, UserAssist |
| **Kara kutu** — önerilen | **yok** | **Windows'un kendisinin** çalıştırdığı iki ETW AutoLogger, tavanı belli dairesel dosyalara yazar - komut satırları dahil. Bellekte bize ait hiçbir şey yok, boştayken CPU maliyeti yok ve açtığın anda kaydetmeye başlar |
| **Canlı** | yalnızca açıkken | `wymcmd watch` ya da pencere açıkken gerçek zamanlı çekirdek izleme |
| **Tuzak** | süresi dolana kadar | "Bir daha olursa yakala", süreli; süre bitince kendini kapatır |
| **Nöbet servisi** | evet, isteğe bağlı | 7/24 kural uygulaması isteyenler için |

```console
wymcmd doctor            # bu makine şu an neyi söyleyebiliyor
wymcmd sources enable    # Windows süreç oluşturmayı komut satırıyla kaydetsin
wymcmd blackbox on       # yerleşik süreç gerektirmeyen kaydedici
wymcmd blackbox read     # kaydedicide şu an ne var
```

## Hızlı başlangıç

```console
wymcmd install             # PATH'e koy (kullanıcı bazlı, yönetici gerekmez)
wymcmd doctor              # ne var, ne eksik
wymcmd sources enable      # tek seferlik, yönetici gerekir, tamamen geri alınabilir
wymcmd blackbox on         # isteğe bağlı: bir daha hiçbir şey kaçmasın, yerleşik süreç olmadan
wymcmd why last            # o konsolu ne açtı?
wymcmd                     # pencere
```

Arkandan hiçbir şey açılmıyor: makineyi değiştiren tek iki komut `sources enable` ve
`blackbox on`, ikisi de açıkça isteniyor, `wymcmd uninstall --purge` hepsini geri alıyor.

## Gerçekte ne çıkarıyor

- **Kim başlattı** — kök sürece kadar tüm üst zincir, çoktan kapanmış üstler dahil
- **Neden başladı** — Zamanlanmış Görev (adıyla), Run anahtarı, Başlangıç klasörü, servis, WMI
  aboneliği, Image File Execution Options, kurulum programı, Office belgesi, tarayıcı indirmesi,
  bir terminal ya da senin çift tıklaman
- **Ne çalıştırdı** — `-EncodedCommand` çözülüp gerçek script gösterilir, `cmd /c` sarması
  açılır, PowerShell kaydından gerçek script bloğu getirilir
- **Penceresi var mıydı** — penceresiz bir konsol, bir şeyin görünmek istemediğinin en güçlü
  işaretidir. Katalog imzalı Windows dosyaları doğru tanınır; sistem araçları asla "imzasız"
  diye yaftalanmaz
- **Bu dosya burada tanıdık mı** — Prefetch çalıştırma sayısını ve son sekiz çalışmayı, BAM tam
  son çalışma zamanını, AmCache ise dosyanın bu makinede ilk görüldüğü günü ve SHA-1'ini verir.
  "Her sabah çalışıyor" ile "yirmi dakika önce belirdi" ayrı cevaplardır
- **Nereye uzandı** — Sysmon'un o süreç için kaydettiği bağlantılar ve DNS sorguları. Bunu süreç
  bazında yalnız Sysmon kaydeder; yoksa bölüm hiç görünmez
- **Ne kadar endişelenmeli** — 0-100 arası skor, ama her zaman gerekçeleri açık
- **Adı ne** — kanıtı zaten elde olan MITRE ATT&CK teknikleri; açılış aranabilsin, bir tespit
  kuralıyla eşleştirilebilsin, ticket'a yapıştırılabilsin diye. Tahmin yok: teknik ancak
  arkasındaki bulgu elde olduğunda çıkar

## Komutlar

```console
wymcmd                          # pencere
wymcmd why <pid|last>           # tek bir açılışı açıkla, gerekirse geçmişe dönük
wymcmd timeline 14:22           # o anın etrafında olan her şey
wymcmd list --last 24h --console --hidden --unsigned --risk 50
wymcmd watch --console          # bu pencere açık kaldığı sürece canlı akış
wymcmd trap --image cmd.exe --hidden-only --for 2h --action killtree
wymcmd tree [pid]
wymcmd kill <pid> [--tree]
wymcmd rules add --image cmd.exe --match "downloadstring" --action kill
wymcmd rules test               # kuralların kayıtlı geçmişte ne yapmış olacağı
wymcmd export --since 24h --format csv|jsonl|report [--forensic]
wymcmd blackbox on|off|status|read
wymcmd sources enable|status
wymcmd service install|start|stop|uninstall
wymcmd doctor
wymcmd coverage --last 7d      # ne zaman izleniyordu, ne zaman izlenmiyordu
wymcmd install                  # wymcmd'yi PATH'e koy (kullanıcı bazlı, yönetici gerekmez)
wymcmd prune [--days N] [--max-mb N]
wymcmd uninstall --purge        # her değişikliği geri al, her dosyayı sil
```

Her komut `--json` (makine-okunur, anahtarlar hep İngilizce) ve `--lang en|tr` destekler.
Çıkış kodları anlamlı: `0` tamam, `2` yönetici gerekiyor, `3` veri kaynağı kapalı, `4` eşleşme yok.

## İzlendi mi, sonradan mı çıkarıldı

İkisi de gerçek cevap ama aynı cevap değil. wymcmd, gerçekten kayıt yapan bir şeyin — pencerenin,
nöbetçi servisin — hangi aralıklarda çalıştığını tutuyor ve her aralığı kalp atışıyla kapatıyor.
Böylece elektrik kesilerek biten bir oturum bile kapsamının nerede durduğunu dakikası dakikasına
biliyor; kapalı bir bilgisayarı izlediğini iddia etmiyor.

```console
wymcmd coverage --last 7d
```

Kaydedilen aralıkları ve ayrıca gerçekten kör olan aralıkları yazar. İkisi aynı şey değil: kayıt
olmayan bir saat ancak makine o sırada açıksa aleyhine sayılır, Windows da açılış/uyku geçişlerini
System günlüğüne yazıyor ve orayı her kullanıcı okuyabiliyor. Hafta sonu kapalı duran bir dizüstü
izlenmemiş sayılmaz; yüzde, makinenin gerçekten açık olduğu süreye göre hesaplanır.

Kara kutu da bir izleyici sayılır — Windows onu açılışta kendisi başlatıyor ve bizden hiçbir şey
çalışmıyor, yani pencere kapalıyken geçen süreyi o kapatıyor. Ne kadar geriye yettiği, oturumun ne
zaman kurulduğundan değil izin gerçekte nereye kadar uzandığından okunuyor; dosya döngüsel ve başa
sarıyor.

`wymcmd why` de bunu söyler: hiçbir şeyin kayıt yapmadığı bir ana ait açıklama, kayıttan okunmuş
değil Windows'un tuttuklarından yeniden kurulmuş olarak işaretlenir.

## Kurallar

Kurallar yalnızca canlı, tuzak ve nöbet modunda çalışır — izlemek tek başına makineni değiştirmez.

```console
wymcmd rules add --image powershell.exe --hidden --unsigned --action killtree --name "gizli imzasız kabuk"
```

Eşleşme alanları: dosya adı, yol, komut satırı regex'i, üst süreç, zincirdeki herhangi bir ata,
imzalayan, kullanıcı, oturum, pencere durumu, yükseltilmişlik, temp yolu ve risk skoru.
Aksiyonlar: `log`, `notify`, `hide`, `suspend`, `kill`, `killtree` ve beyaz liste için `allow`.
Kural eklendiği anda kayıtlı geçmişte kaç kez eşleşeceği gösterilir — kör kural kurulmaz.

`csrss.exe`, `lsass.exe`, `services.exe`, `winlogon.exe` ve benzerlerini hiçbir bayrakla
atlatılamayan bir koruma reddeder.

## Nasıl biliyor

Her alan nereden geldiğini taşır, cevap da ne kadar emin olduğunu söyler.

| Kanıt | Ne veriyor | Ne gerekiyor |
|---|---|---|
| ETW çekirdek izleme | Her açılışı, komut satırıyla, 30 ms'lik olanı bile | yönetici, izlerken |
| Kara kutu (AutoLogger) | Aynısını geçmiş için, yerleşik süreç olmadan - iki oturum, biri komut satırını da taşıyor | tek seferlik kurulum, yönetici |
| Güvenlik günlüğü 4688/4689 | Açılış, üst süreç, komut satırı, çıkış kodu | `wymcmd sources enable` |
| Sysmon olay 1 | Hash, üst komut satırı, bütünlük seviyesi | Sysmon kuruluysa |
| Görev Zamanlayıcı günlüğü | Açılışın arkasındaki görev adını, pid üzerinden | `wymcmd sources enable` |
| PowerShell 4104 | Gerçekte çalışan script, gizlenmemiş hâliyle | `wymcmd sources enable` |
| Prefetch | Çalıştırma sayısı ve son sekiz çalışma zamanı, dosyadan çözülerek | yönetici |
| BAM / UserAssist | Kullanıcı başına tam son çalışma; kabuktan neyin başlatıldığı | BAM için yönetici |
| AmCache | Bu makinenin dosyayı ilk kataloglama zamanı ve SHA-1'i | yönetici |
| WMI yoklama | Başka hiçbir şey yokken yedek | hiçbir şey — ve neyi kaçırdığını söyler |

SRUM bilerek okunmuyor: bir ESE veritabanı ve buraya katkısı uygulama başına kaynak kullanımı
olurdu; bu da bir konsolun neden açıldığını anlatmıyor.

Sonuç `kesin`, `yüksek` ya da `çıkarım` olarak etiketlenir; detay panelinde her alanın hangi
kaynaktan geldiği görünür. Boşluk doldurmak için hiçbir şey uydurulmaz.

## Gizlilik

Her şey makinende kalır: `%ProgramData%\wymcmd` (yönetici değilken `%LOCALAPPDATA%\wymcmd`)
altında tek bir SQLite veritabanı. Telemetri yok, ağ çağrısı yok, otomatik güncelleme yok —
ikili dosyanın içinde hiç HTTP istemcisi yok, yani kapatılacak bir şey de yok.

`WYMCMD_HOME` tanımlarsan her şey gösterdiğin yerde durur: bir bellek, tek bir incelemeye ait bir
klasör, sonra silinecek bir kum havuzu.

Bilerek unutur da: varsayılan 30 gün ve 256 MB, ikisi de `settings.json` içinde; arka planda ve
istendiğinde `wymcmd prune` ile uygulanır. Kara kutu izleri oluşturulurken tavanı belirlenir,
o tavanın üstüne çıkmaz.

`wymcmd uninstall --purge` yaptığı denetim politikası değişikliklerini geri alır (yalnız kendi
yaptıklarını — bir günlük tutuyor), kara kutuyu ve kaydını kaldırır, servisi siler, veriyi siler.

## Dil

Kaynak dil İngilizce, Türkçe tam çeviri — pencere, CLI çıktısı, yardım metni, hata mesajları ve
dışa aktarılan raporlar dahil. `--lang tr` ya da penceredeki EN/TR anahtarı.

## Kurulum

[Scoop](https://scoop.sh) ile — güncellemeleri de kendi getirir:

```console
scoop bucket add tlk https://github.com/Talkdedsec/scoop-tlk
scoop install tlk/wymcmd
```

Ya da [Releases](https://github.com/Talkdedsec/tlk-wymcmd/releases) sayfasından zip'i indir,
istediğin yere aç ve şunu çalıştır:

```console
wymcmd install
```

İki dosyayı `%LOCALAPPDATA%\Programs\wymcmd` altına kopyalar, o klasörü PATH'e ekler ve başlat
menüsüne kısayol koyar. Yönetici yok, kurulum sihirbazı yok; geri almak da `wymcmd uninstall`.
Sonrasında yeni bir terminal aç, `wymcmd` her klasörde çalışır.

Taşınabilir kalsın mı istiyorsun? Kurulumu atla, açtığın yerden çalıştır. Çalıştırılabilir dosya
kendi kendine yeter, iki durumda da indirilecek bir runtime yok.

Zip'in içinde birbirine ait iki dosya var:

| Dosya | Ne işe yarıyor |
|---|---|
| `wymcmd.exe` | Aracın kendisi. Çift tıklarsan pencere açılır. |
| `wymcmd.com` | 1 MB'lık konsol başlatıcısı. Windows kabukları `.com` uzantısını `.exe`'den önce çözer; `wymcmd list` yazdığında bu çalışır, aracın bitmesini bekler ve çıkış kodunu geri verir. O olmadan kabuk komut istemini hemen döndürür ve yönlendirdiğin çıktı yarışa girer. |

İkili dosya kod imzalı değil; SmartScreen ilk açılışta "tanınmayan uygulama" diyecek: "Daha fazla
bilgi" sonra "Yine de çalıştır". Dosyaya isimden değil hash'ten güvenmek istersen her sürümde
yanında bir `.sha256` var.

ARM64 zip'i de yayımlanıyor, aynı kaynaktan derleniyor. ARM makinede test koşulmuyor, onu test
edilmemiş kabul et.

## Kaynaktan derleme

[.NET 10 SDK](https://dotnet.microsoft.com/download) gerekir. Başlatıcı önceden derlendiği için
Visual Studio C++ build tools ister ve derleyici `vswhere.exe`'yi PATH'te bekler
(`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`). Yalnız pencereyi istiyorsan o adımı
atlayabilirsin.

```console
git clone https://github.com/Talkdedsec/tlk-wymcmd
cd wymcmd
dotnet publish src/Wymcmd/Wymcmd.csproj -c Release -o publish
dotnet publish src/WymcmdShim/WymcmdShim.csproj -c Release -o launcher
copy launcher\wymcmd-launcher.exe publish\wymcmd.com
```

```console
dotnet test src/Wymcmd.Tests/Wymcmd.Tests.csproj
```

Depo düzeni: `src/Wymcmd/Core` yakalama, adli katman, atıf, kurallar ve depolamayı barındırır;
`Cli` ile `Views`/`ViewModels` aynı motorun iki yüzüdür; `src/Wymcmd.Tests` izlenecek bir makine
olmadan test edilebilen kısmı kapsar; `scripts/` çeviri kapısını, senaryo üreticisini ve yakalama
yük testini taşır.

## Lisans

Kaynağı açık, **açık kaynak değil**: kullanımı serbest, **değiştirme yok, yeniden dağıtma yok,
satma yok**. Bkz. [LICENSE](LICENSE).

<p align="center">
  <sub><a href="https://talkdedsec.com">Talkdedsec</a> tarafından yapıldı</sub>
</p>
