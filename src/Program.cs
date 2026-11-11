using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace GcmsPorovnanie;

sealed class DoubleConv : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType == JsonTokenType.Null ? double.NaN : r.GetDouble();
    public override void Write(Utf8JsonWriter w, double v, JsonSerializerOptions o)
    { if (double.IsFinite(v)) w.WriteNumberValue(Math.Round(v, 6)); else w.WriteNullValue(); }
}

sealed class Uloha
{
    public string Id = Guid.NewGuid().ToString("N")[..12];
    public volatile string Stav = "čaká";
    public volatile int Hotovo, Spolu;
    public string? Chyba;
    public List<Vysledok>? Vysledky;
    public byte[]? Excel;
    public Nastavenia Nast = new();
    public string Dir = "";
    public string? Alkany;
    public List<string> Kniznice = new();
}

public static class Program
{
    public static readonly CultureInfo Sk = CultureInfo.InvariantCulture;
    public static readonly string Verzia = "1.1.0";
    public static string KnizniceDir => Path.Combine(AppContext.BaseDirectory, "kniznice");
    static readonly ConcurrentDictionary<string, Uloha> Ulohy = new();
    static readonly JsonSerializerOptions Js = new() { Converters = { new DoubleConv() }, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            if (args.Length > 0 && args[0] == "--test-cielene") return CielenaAnalyzaTest.Run();
            if (args.Length > 0 && args[0] == "--cielene") return CielenaAnalyza.Run(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--dump") return Dump(args[1], args[2]);
            if (args.Length > 0 && args[0] == "--porovnaj") return Cli(args.Skip(1).ToArray());
            Server(args);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Chyba: " + e.Message);
            Console.Error.WriteLine(e);
            if (args.Length == 0) { Console.WriteLine("Stlačte Enter na zatvorenie."); Console.ReadLine(); }
            return 1;
        }
    }

    // --dump subor.raw vystup.csv : časy, TIC a súčet spektra (na overenie načítania)
    static int Dump(string f, string outp)
    {
        var m = Meranie.Nacitaj(f, Path.GetFileName(f));
        using var w = new StreamWriter(outp);
        w.WriteLine($"# mz {m.MzMin} {m.MzMax}; " + string.Join("; ", m.Info.Select(kv => kv.Key + "=" + kv.Value)));
        for (int i = 0; i < m.N; i++)
            w.WriteLine(string.Join(",", new[] { m.Rt[i], m.Tic[i], m.Spec[i].Sum() }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)))
                        + "," + string.Join(" ", m.Spec[i].Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
        Console.WriteLine($"{m.N} skenov, m/z {m.MzMin}–{m.MzMax}");
        return 0;
    }

    // --porovnaj standard vzorka1 [vzorka2 ...] --out vysledok.xlsx [--json vysledok.json] [--bez-dekonvolucie]
    //   [--kniznica subor.msp|nazov_prilozenej ...] [--alkany meranie.raw] [--prvy-alkan 10] [--ri alkany|odhad|ziadny] [--mz-od 85]
    static int Cli(string[] a)
    {
        var files = new List<string>(); string? outp = null, json = null, alk = null; var o = new Nastavenia();
        var kniz = new List<string>(); string? ri = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--out") outp = a[++i];
            else if (a[i] == "--json") json = a[++i];
            else if (a[i] == "--bez-dekonvolucie") o.Dekonvolucia = false;
            else if (a[i] == "--kniznica") kniz.Add(a[++i]);
            else if (a[i] == "--alkany") alk = a[++i];
            else if (a[i] == "--prvy-alkan") o.PrvyAlkan = int.Parse(a[++i], CultureInfo.InvariantCulture);
            else if (a[i] == "--ri") ri = a[++i];
            else if (a[i] == "--mz-od") o.MzOd = int.Parse(a[++i], CultureInfo.InvariantCulture);
            else files.Add(a[i]);
        }
        o.NavrhLatok = kniz.Count > 0;
        o.RiZdroj = ri ?? (alk != null ? "alkany" : o.NavrhLatok ? "odhad" : "ziadny");
        var cesty = kniz.Select(k => File.Exists(k) ? k : Path.Combine(KnizniceDir, Path.GetFileName(k))).ToList();
        foreach (var c in cesty) if (!File.Exists(c)) { Console.Error.WriteLine("Knižnica neexistuje: " + c); return 2; }
        if (files.Count < 2) { Console.Error.WriteLine("Treba štandard a aspoň jednu vzorku."); return 2; }
        var sw = Stopwatch.StartNew();
        var S = Meranie.Nacitaj(files[0], Path.GetFileName(files[0]));
        var (K, A) = PripravIdentifikaciu(o, cesty.Select(c => (c, Path.GetFileName(c))).ToList(), alk);
        if (A != null) { Console.WriteLine("Alkány: " + string.Join(", ", A.Alkany.Select(x => $"C{x.C} {x.RtMin:0.00}"))); foreach (var w in A.Varovania) Console.WriteLine("  " + w); }
        var specS = (K != null || A != null) ? Identifikacia.PripravSpektra(S, o) : null;
        var vys = new List<Vysledok>();
        foreach (var f in files.Skip(1))
        {
            var V = Meranie.Nacitaj(f, Path.GetFileName(f));
            var r = Porovnanie.Porovnaj(S, V, o);
            if (specS != null) Identifikacia.Dopln(r, S, specS, V, K, A, o);
            vys.Add(r);
            Console.WriteLine($"{r.Vzorka}: {r.Zaver}  ({sw.Elapsed.TotalSeconds:0.0} s)");
            if (r.RiInfo != null) Console.WriteLine("  " + r.RiInfo);
        }
        if (outp != null) File.WriteAllBytes(outp, Excel.Zostav(vys, o));
        if (json != null) File.WriteAllText(json, JsonSerializer.Serialize(vys, Js));
        return 0;
    }

    /// <summary>Načíta knižnice (.msp) a meranie alkánov podľa nastavení.</summary>
    static (Kniznica? k, AlkanovaKalibracia? a) PripravIdentifikaciu(Nastavenia o, List<(string cesta, string meno)> nahrate, string? alkany)
    {
        Kniznica? K = null;
        if (o.NavrhLatok)
        {
            K = new Kniznica();
            foreach (var nm in o.Kniznice ?? Array.Empty<string>())
            {
                var p = Path.Combine(KnizniceDir, Path.GetFileName(nm));
                if (File.Exists(p)) K.Pridaj(p);
            }
            foreach (var (c, m) in nahrate) K.Pridaj(c, m);
            if (K.Zaznamy.Count == 0) throw new InvalidDataException("Návrh látok je zapnutý, ale nie je vybraná žiadna knižnica (alebo knižnica neobsahuje spektrá).");
        }
        AlkanovaKalibracia? A = null;
        if (alkany != null && o.RiZdroj == "alkany") A = AlkanovaKalibracia.Najdi(Meranie.Nacitaj(alkany, Path.GetFileName(alkany)), o);
        return (K, A);
    }

    static void Server(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ContentRootPath = baseDir, WebRootPath = Path.Combine(baseDir, "wwwroot"),
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            k.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.Configure<FormOptions>(f => { f.MultipartBodyLengthLimit = long.MaxValue; f.ValueLengthLimit = int.MaxValue; });
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // súhlas s licenciou Thermo RawFileReader (bod 3.3) – uložený v priečinku používateľa
        var licDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GcmsPorovnanie");
        var licFile = Path.Combine(licDir, "licencia-thermo-prijata.txt");
        var licencie = Path.Combine(baseDir, "LICENCIE");
        app.MapGet("/api/licencia", () => Results.Json(new
        {
            ok = File.Exists(licFile),
            subory = Directory.Exists(licencie) ? Directory.GetFiles(licencie).Select(Path.GetFileName).ToArray() : Array.Empty<string?>(),
        }, Js));
        app.MapGet("/api/licencia/{nazov}", (string nazov) =>
        {
            var p = Path.Combine(licencie, Path.GetFileName(nazov));
            if (!File.Exists(p)) return Results.NotFound();
            var typ = p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain; charset=utf-8" : "application/msword";
            return Results.File(File.ReadAllBytes(p), typ, Path.GetFileName(p));
        });
        app.MapPost("/api/licencia/suhlas", () =>
        {
            Directory.CreateDirectory(licDir);
            File.WriteAllText(licFile, $"Licencia Thermo RawFileReader prijatá {DateTime.Now:yyyy-MM-dd HH:mm:ss}, verzia programu {Verzia}{Environment.NewLine}");
            return Results.Ok();
        });

        app.MapPost("/api/porovnaj", async (HttpRequest req) =>
        {
            if (!File.Exists(licFile)) return Results.Json(new { chyba = "Najprv prijmite licenčné podmienky (O programe)." }, Js, statusCode: 403);
            var form = await req.ReadFormAsync();
            var std = form.Files.GetFile("standard");
            var vz = form.Files.GetFiles("vzorky");
            if (std == null || vz.Count == 0) return Results.BadRequest(new { chyba = "Nahrajte štandard a aspoň jednu vzorku." });
            var u = new Uloha();
            try
            {
                var nj = form["nastavenia"].ToString();
                if (!string.IsNullOrWhiteSpace(nj)) u.Nast = JsonSerializer.Deserialize<Nastavenia>(nj, Js) ?? new Nastavenia();
            }
            catch { return Results.BadRequest(new { chyba = "Nesprávne nastavenia." }); }
            if (u.Nast.Tol <= 0 || u.Nast.Krok <= 0 || u.Nast.Krok > u.Nast.Tol) return Results.BadRequest(new { chyba = "Tolerancia a krok musia byť kladné a krok ≤ tolerancia." });
            u.Dir = Path.Combine(Path.GetTempPath(), "gcms-porovnanie", u.Id);
            Directory.CreateDirectory(u.Dir);
            async Task<string> Uloz(IFormFile f, string pod)
            {
                var d = Path.Combine(u.Dir, pod); Directory.CreateDirectory(d);
                var p = Path.Combine(d, Path.GetFileName(f.FileName));
                await using var s = File.Create(p); await f.CopyToAsync(s); return p;
            }
            var pStd = await Uloz(std, "std");
            var pVz = new List<string>(); int ix = 0;
            foreach (var f in vz) pVz.Add(await Uloz(f, "vz" + ix++));
            var alk = form.Files.GetFile("alkany");
            if (alk != null && u.Nast.RiZdroj == "alkany") u.Alkany = await Uloz(alk, "alk");
            int kx = 0;
            foreach (var f in form.Files.GetFiles("kniznice")) u.Kniznice.Add(await Uloz(f, "kn" + kx++));
            if (u.Nast.NavrhLatok && u.Nast.Kniznice.Length == 0 && u.Kniznice.Count == 0)
                return Results.BadRequest(new { chyba = "Návrh látok je zapnutý – vyberte aspoň jednu knižnicu." });
            if (u.Nast.RiZdroj == "alkany" && u.Alkany == null)
                return Results.BadRequest(new { chyba = "Zvolili ste RI z merania alkánov – nahrajte meranie alkánov (alebo zvoľte inú možnosť RI)." });
            u.Spolu = pVz.Count + 1 + (u.Alkany != null ? 1 : 0);
            Ulohy[u.Id] = u;
            _ = Task.Run(() => Spracuj(u, pStd, pVz));
            return Results.Json(new { id = u.Id }, Js);
        });
        app.MapGet("/api/stav/{id}", (string id) =>
            Ulohy.TryGetValue(id, out var u)
                ? Results.Json(new { stav = u.Stav, hotovo = u.Hotovo, spolu = u.Spolu, chyba = u.Chyba, koniec = u.Vysledky != null || u.Chyba != null }, Js)
                : Results.NotFound());
        app.MapGet("/api/vysledok/{id}", (string id) =>
            Ulohy.TryGetValue(id, out var u) && u.Vysledky != null ? Results.Json(new { vysledky = u.Vysledky, nastavenia = u.Nast }, Js) : Results.NotFound());
        app.MapGet("/api/excel/{id}", (string id) =>
        {
            if (!Ulohy.TryGetValue(id, out var u) || u.Excel == null) return Results.NotFound();
            var nm = u.Vysledky!.Count == 1
                ? $"porovnanie_{Path.GetFileNameWithoutExtension(u.Vysledky[0].Vzorka)}_vs_{Path.GetFileNameWithoutExtension(u.Vysledky[0].Standard)}.xlsx"
                : $"porovnanie_{u.Vysledky.Count}_vzoriek_vs_{Path.GetFileNameWithoutExtension(u.Vysledky[0].Standard)}.xlsx";
            return Results.File(u.Excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nm);
        });
        app.MapGet("/api/kniznice", () =>
        {
            var d = KnizniceDir;
            var list = Directory.Exists(d)
                ? Directory.GetFiles(d, "*.msp").OrderBy(f => f).Select(f => new
                  {
                      nazov = Path.GetFileName(f),
                      spektier = File.ReadLines(f).Count(l => l.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase)),
                  }).ToArray()
                : Array.Empty<object>().Select(x => new { nazov = "", spektier = 0 }).ToArray();
            return Results.Json(list, Js);
        });
        app.MapGet("/api/info", () => Results.Json(new { verzia = Verzia, nastavenia = new Nastavenia() }, Js));
        app.MapPost("/api/koniec", (IHostApplicationLifetime life) => { _ = Task.Run(async () => { await Task.Delay(300); life.StopApplication(); }); return Results.Ok(); });

        app.Start();
        var addr = app.Urls.First().Replace("127.0.0.1", "localhost");
        Console.WriteLine("GC-MS porovnanie " + Verzia);
        Console.WriteLine("Program beží na " + addr);
        Console.WriteLine("Ak sa prehliadač neotvoril sám, otvorte túto adresu ručne.");
        Console.WriteLine("Program ukončíte zatvorením tohto okna alebo tlačidlom „Ukončiť“ v prehliadači.");
        if (Environment.GetEnvironmentVariable("GCMS_NO_BROWSER") != "1") OtvorPrehliadac(addr);
        app.WaitForShutdown();
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "gcms-porovnanie"), true); } catch { }
    }

    static void Spracuj(Uloha u, string pStd, List<string> pVz)
    {
        try
        {
            u.Stav = "Načítavam štandard " + Path.GetFileName(pStd);
            var S = Meranie.Nacitaj(pStd, Path.GetFileName(pStd));
            u.Hotovo = 1;
            if (u.Nast.NavrhLatok) u.Stav = "Načítavam knižnice spektier";
            if (u.Alkany != null) u.Stav = "Hľadám alkány v " + Path.GetFileName(u.Alkany);
            var (K, A) = PripravIdentifikaciu(u.Nast, u.Kniznice.Select(c => (c, Path.GetFileName(c))).ToList(), u.Alkany);
            if (u.Alkany != null) u.Hotovo++;
            var specS = (K != null || A != null) ? Identifikacia.PripravSpektra(S, u.Nast) : null;
            var vys = new List<Vysledok>();
            for (int i = 0; i < pVz.Count; i++)
            {
                var nm = Path.GetFileName(pVz[i]);
                u.Stav = $"Porovnávam vzorku {nm} ({i + 1}/{pVz.Count})";
                var V = Meranie.Nacitaj(pVz[i], nm);
                var r = Porovnanie.Porovnaj(S, V, u.Nast);
                if (specS != null) { u.Stav = $"Hľadám látky v knižnici – {nm}"; Identifikacia.Dopln(r, S, specS, V, K, A, u.Nast); }
                vys.Add(r);
                u.Hotovo++;
            }
            u.Stav = "Pripravujem Excel";
            u.Excel = Excel.Zostav(vys, u.Nast);
            u.Vysledky = vys;
            u.Stav = "Hotovo";
        }
        catch (Exception e)
        {
            u.Chyba = e.Message;
            u.Stav = "Chyba";
        }
        finally
        {
            try { Directory.Delete(u.Dir, true); } catch { }
        }
    }

    static void OtvorPrehliadac(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { }
    }
}
