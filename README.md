# Skärmstyrning

Styr dina skärmar från en plats: utöka, duplicera eller visa på en enda skärm, byt ingång
(HDMI/DP/DVI) via DDC/CI, spara profiler med kortkommandon – och låt mus och tangentbord
följa med över skärmkanten till en annan dator, utan KVM-switch.

```
SkarmStyrning/          Huvudappen (datorn med skärmarna). WinForms + WebView2-gränssnitt.
SkarmStyrning.Client/   Klienten som körs på den andra datorn (t.ex. jobbdatorn).
Shared/Net/             Gemensam kod: krypterat protokoll och skärmläsning.
```

## Ladda ner färdig version

Under [Releases](../../releases) finns färdigbyggda versioner som inte kräver .NET:

- **SkarmStyrningKlient.exe** – klienten för den andra datorn (t.ex. jobbdatorn). En enda fil, kör den direkt.
- **Skarmstyrning.zip** – huvudappen. Packa upp och kör `SkarmStyrning.exe`.

Filerna är inte signerade, så Windows SmartScreen kan varna: välj *Mer information → Kör ändå*.

Versionerna byggs av GitHub Actions (`.github/workflows/release.yml`) när en tagg som `v1.0.0` pushas.
Arbetsflödet kan också köras manuellt under *Actions*; då hamnar filerna som en artefakt på körningen.

## Krav

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) för att bygga
- Huvudappen använder Microsoft Edge WebView2 (finns redan i Windows 11)

## Bygga och köra

Huvudappen:

```bash
dotnet run -c Release --project SkarmStyrning
```

Klienten (på jobbdatorn):

```bash
dotnet run -c Release --project SkarmStyrning.Client
```

Vill du ha en fristående .exe som inte kräver .NET på datorn:

```bash
dotnet publish SkarmStyrning.Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish/klient
```

## Koppla ihop jobbdatorn

1. Starta huvudappen och tryck **Lägg till dator**. En sexsiffrig kod och datorns adress visas.
2. Starta klienten på jobbdatorn. Skriv in adressen (eller tryck **Sök**) och koden, tryck **Parkoppla**.
3. Klart. Klienten ansluter automatiskt framöver (bocka gärna i *Starta automatiskt*).

Första gången frågar Windows brandvägg på huvuddatorn – tillåt **privata nätverk**.
Klienten ansluter till TCP-port **47811**; sökningen använder UDP-port **47812**.

### VPN

Många jobb-VPN:er stänger av åtkomsten till det lokala nätverket. Tryck **Testa** i klienten:

- *Servern svarar* – allt fungerar.
- *Nätverket når inte servern* – VPN:et blockerar lokala nätverket. Fråga IT om "local LAN access"
  eller split tunneling, eller koppla ner VPN:et när du vill använda delningen.

## Så fungerar delningen

- Varje skärm i layouten **visar** en dator. Appen räknar ut vilken via ingången (DDC/CI) och vad
  klienten rapporterar – eller så väljer du själv under *Dator på skärmen*.
- När pekaren går över kanten till en skärm som visar jobbdatorn fångar huvudappen mus och
  tangentbord och skickar dem krypterat (ECDH P-256 + AES-GCM) till klienten.
- **Ctrl+Shift+Alt + valfri tangent** tar alltid tillbaka musen.
- **Delat urklipp:** text du kopierar på en dator finns direkt att klistra in på de andra (krypterat).
  Stäng av under *Inställningar → Andra datorer → Dela urklipp*; då skickar klienten ingenting.
- Byt ingång med en profil så följer delningen automatiskt med.

Begränsningar: Ctrl+Alt+Del går inte att skicka, och klienten kan inte styra fönster som körs som
administratör om inte klienten också startas som administratör.

## Kommandorad (huvudappen)

```
SkarmStyrning.exe --extend | --clone | --only "BenQ" | --profile "Spel" | --input "BenQ" HDMI1 | --list | --status
```

Data sparas i `%APPDATA%\SkarmStyrning` (huvudappen) och `%APPDATA%\SkarmStyrningKlient` (klienten).
Nycklar sparas krypterade med Windows DPAPI.
