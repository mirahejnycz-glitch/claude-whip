# DesktopWhip

Nativní Windows overlay s fyzikálně simulovaným bičem (provaz = Verlet řetěz bodů).
Čistě vizuální — nezasahuje do oken ani procesů. Bič přilne ke kurzoru a švihá podle
rychlosti pohybu myši. Overlay je click-through, takže ti nebrání normálně pracovat.

## Build — varianta A: dotnet SDK (doporučeno)

Potřebuješ .NET 8 SDK (https://dotnet.microsoft.com/download).

```
cd WhipApp
dotnet build -c Release
```

Spustitelný soubor: `bin\Release\net8.0-windows\DesktopWhip.exe`

Nebo rovnou:
```
dotnet run -c Release
```

## Build — varianta B: jen csc.exe (bez SDK, přes .NET Framework)

Pokud máš jen klasický .NET Framework (každý Windows ho má):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:DesktopWhip.exe Program.cs
```

(Smaž/ignoruj `DesktopWhip.csproj`, ten je jen pro variantu A.)

## Ovládání

- Pohybuj myší — bič tě následuje a švihá.
- Rychlý pohyb = "crack" efekt (zesvětlení špičky).
- **ESC** = konec.

## Ladění (v Program.cs)

- `SEGMENTS` — počet článků (delší/kratší bič)
- `segLen` — délka článku
- `GRAVITY` — gravitace
- `DAMPING` — tlumení (1.0 = bez tření, hodně rozmáchlý)
- `CONSTRAINT_ITERS` — tuhost provazu (víc = méně pružný)

## Poznámka k „tahání myší"

Chtěl jsi ovládání tahem. Click-through overlay ale nemůže zachytávat kliknutí
(jinak by ti bránil klikat do plochy). Proto bič jede za kurzorem bez držení tlačítka.
Chceš-li klasický drag (držet levé tlačítko), stačí v `CreateParams` odebrat
`WS_EX_TRANSPARENT` a v `Step()` číst pozici jen při stisknutém tlačítku — řekni a upravím.
