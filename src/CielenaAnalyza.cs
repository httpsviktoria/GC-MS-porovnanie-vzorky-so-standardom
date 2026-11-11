using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GcmsPorovnanie;

// Research-only targeted screening. Reuses the application's native RAW/mzML reader.
// Does not confirm chemical identity or calculate concentrations.
public static class CielenaAnalyza
{
    public sealed class Latka
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("reference_file")] public string ReferenceFile { get; set; } = "";
        [JsonPropertyName("reference_status")] public string ReferenceStatus { get; set; } = "UNCONFIRMED";
        [JsonPropertyName("quant_ion")] public int QuantIon { get; set; }
        [JsonPropertyName("qualifier_ions")] public int[] QualifierIons { get; set; } = Array.Empty<int>();
        [JsonPropertyName("rt_min")] public double RtMin { get; set; }
        [JsonPropertyName("rt_max")] public double RtMax { get; set; }
        [JsonPropertyName("min_screening_cosine")] public double MinCosine { get; set; } = 0.8;
        [JsonPropertyName("min_apex_qualifier_fraction")] public double MinQualifierFraction { get; set; } = 0.001;
    }

    public sealed class Konfiguracia
    {
        [JsonPropertyName("analytes")] public List<Latka> Analytes { get; set; } = new();
    }

    public sealed class Riadok
    {
        public string File { get; set; } = "";
        public string Analyte { get; set; } = "";
        public string ReferenceStatus { get; set; } = "";
        public string Status { get; set; } = "NOT_EVALUABLE";
        public string Reason { get; set; } = "";
        public double? RtMin { get; set; }
        public double? CosineMasked { get; set; }
        public double? AreaQuant { get; set; }
        public double? SnrApprox { get; set; }
        public double? QualifierToQuantApex { get; set; }
        public int? QualifiersDetected { get; set; }
        public int? QualifiersTotal { get; set; }
        public double? PeakStartMin { get; set; }
        public double? PeakEndMin { get; set; }
        public string Integration { get; set; } = "automatic screening boundaries; unvalidated";
        public string Concentration { get; set; } = "NOT_CALCULATED";
    }

    static double Median(IEnumerable<double> xs)
    {
        var a = xs.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (a.Length == 0) return 0;
        int n = a.Length;
        return (n % 2 == 1) ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    static Dictionary<int, double> ReadReference(string path)
    {
        var dict = new Dictionary<int, double>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            var cols = line.Trim().Split(',');
            if (cols.Length < 2) continue;
            if (int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ion)
                && double.TryParse(cols[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value) && value >= 0) dict[ion] = value;
        }
        if (dict.Count == 0 || dict.Values.Max() <= 0) throw new InvalidDataException("Neplatne referencne spektrum CSV.");
        return dict;
    }

    // Trapezoidal integration of the baseline-subtracted EIC, no intensity extrapolation.
    static double Area(double[] rt, double[][] sp, int mzIndex, double baseline, int first, int last)
    {
        double sum = 0;
        for (int k = first; k < last; k++)
        {
            double y1 = Math.Max(0, sp[k][mzIndex] - baseline);
            double y2 = Math.Max(0, sp[k + 1][mzIndex] - baseline);
            sum += (y1 + y2) * (rt[k + 1] - rt[k]) * 0.5;
        }
        return sum;
    }

    public static Riadok Screen(Meranie m, Latka latka, IReadOnlyDictionary<int, double> reference)
    {
        var row = new Riadok { File = m.Nazov, Analyte = latka.Name, ReferenceStatus = latka.ReferenceStatus };
        var ions = new[] { latka.QuantIon }.Concat(latka.QualifierIons).Distinct().ToArray();
        if (latka.QuantIon <= 0 || latka.RtMax <= latka.RtMin || latka.QualifierIons.Length == 0)
        { row.Reason = "Neplatna konfiguracia analytu."; return row; }
        if (m.N < 20 || m.Rt.Length != m.Spec.Length || m.Rt.Zip(m.Rt.Skip(1)).Any(p => p.First >= p.Second))
        { row.Reason = "Chybajuce alebo neusporiadane skeny."; return row; }
        if (ions.Any(z => z < m.MzMin || z > m.MzMax || !reference.ContainsKey(z)))
        { row.Reason = "Chybajuci diagnosticky ion v referencii alebo merani."; return row; }
        double[] rt = m.Rt;
        double[][] sp = m.Spec;
        int q = latka.QuantIon - m.MzMin;
        var selected = Enumerable.Range(1, m.N - 2)
            .Where(i => rt[i] >= latka.RtMin * 60 && rt[i] <= latka.RtMax * 60)
            .Where(i => sp[i][q] >= sp[i - 1][q] && sp[i][q] > sp[i + 1][q])
            .ToArray();
        if (selected.Length == 0) { row.Status = "NOT_DETECTED_SCREEN"; row.Reason = "V RT okne neexistuje lokalne maximum EIC."; return row; }
        var bestScore = double.NegativeInfinity;
        int bestIndex = -1;
        double bestCos = 0, bestNoise = 1, bestBase = 0;
        int bestQual = 0;
        double bestRatio = 0;
        foreach (int i in selected)
        {
            var flanks = Enumerable.Range(0, m.N).Where(j =>
                Math.Abs(rt[j] - rt[i]) >= 6 && Math.Abs(rt[j] - rt[i]) <= 12).ToArray();
            if (flanks.Length < 4) continue;
            var baseline = ions.ToDictionary(z => z, z => Median(flanks.Select(j => sp[j][z - m.MzMin])));
            double refSq = 0, obsSq = 0, dot = 0;
            foreach (int z in ions)
            {
                double a = reference[z], b = Math.Max(0, sp[i][z - m.MzMin] - baseline[z]);
                refSq += a * a; obsSq += b * b; dot += a * b;
            }
            double cosine = refSq > 0 && obsSq > 0 ? dot / Math.Sqrt(refSq * obsSq) : 0;
            double qIntensity = Math.Max(0, sp[i][q] - baseline[latka.QuantIon]);
            double qMad = Median(flanks.Select(j => Math.Abs(sp[j][q] - baseline[latka.QuantIon])));
            double noise = Math.Max(1, 1.4826 * qMad);
            int nQual = latka.QualifierIons.Count(z =>
                Math.Max(0, sp[i][z - m.MzMin] - baseline[z]) > Math.Max(noise, qIntensity * latka.MinQualifierFraction));
            double score = cosine * Math.Log(1 + qIntensity);
            if (score <= bestScore) continue;
            bestScore = score;
            bestIndex = i;
            bestCos = cosine;
            bestBase = baseline[latka.QuantIon];
            bestNoise = noise;
            bestQual = nQual;
            int firstQual = latka.QualifierIons[0];
            bestRatio = qIntensity > 0 ? Math.Max(0, sp[i][firstQual - m.MzMin] - baseline[firstQual]) / qIntensity : 0;
        }
        if (bestIndex < 0) { row.Reason = "Nedostatocne okolie na odhad pozadia."; return row; }
        int apex = bestIndex;
        double apexSignal = Math.Max(0, sp[apex][q] - bestBase);
        double threshold = Math.Max(3 * bestNoise, apexSignal * 0.01);
        int left = apex, right = apex;
        double maxWindowSeconds = 15;
        while (left > 0 && rt[apex] - rt[left - 1] <= maxWindowSeconds && sp[left - 1][q] - bestBase > threshold) left--;
        while (right < m.N - 1 && rt[right + 1] - rt[apex] <= maxWindowSeconds && sp[right + 1][q] - bestBase > threshold) right++;
        // Include adjacent boundary points when possible (interpolation is not needed for screening).
        if (left > 0) left--;
        if (right < m.N - 1) right++;
        row.RtMin = rt[apex] / 60;
        row.CosineMasked = bestCos;
        row.SnrApprox = apexSignal / bestNoise;
        row.QualifierToQuantApex = bestRatio;
        row.QualifiersDetected = bestQual;
        row.QualifiersTotal = latka.QualifierIons.Length;
        row.PeakStartMin = rt[left] / 60;
        row.PeakEndMin = rt[right] / 60;
        row.AreaQuant = Area(rt, sp, q, bestBase, left, right);
        bool reachedLimit = rt[apex] - rt[left] >= maxWindowSeconds || rt[right] - rt[apex] >= maxWindowSeconds;
        bool intensityOk = row.SnrApprox >= 5;
        if (bestCos >= latka.MinCosine && bestQual >= 1 && intensityOk && !reachedLimit)
        {
            row.Status = "CANDIDATE_REVIEW";
            row.Reason = "Pracovna referencia; nezavisla chemicka identita a hranice integracie vyzaduju kontrolu.";
        }
        else
        {
            row.Status = "REVIEW_REQUIRED";
            row.Reason = $"cos={bestCos:0.000}; kvalifikatory={bestQual}; SNR={row.SnrApprox:0.0}; hranica={reachedLimit}";
        }
        return row;
    }

    static string Escape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    static string Num(double? x) => x.HasValue ? x.Value.ToString("G17", CultureInfo.InvariantCulture) : "";
    static void WriteCsv(string file, List<Riadok> data)
    {
        using var writer = new StreamWriter(file, false, new UTF8Encoding(true));
        writer.WriteLine("file,analyte,reference_status,status,reason,rt_min,cosine_masked,area_quant,snr_approx,qualifier_to_quant_apex,qualifiers_detected,qualifiers_total,peak_start_min,peak_end_min,integration,concentration");
        foreach (var x in data)
            writer.WriteLine(string.Join(",", new[] { Escape(x.File), Escape(x.Analyte), Escape(x.ReferenceStatus), Escape(x.Status), Escape(x.Reason), Num(x.RtMin), Num(x.CosineMasked), Num(x.AreaQuant), Num(x.SnrApprox), Num(x.QualifierToQuantApex), x.QualifiersDetected?.ToString() ?? "", x.QualifiersTotal?.ToString() ?? "", Num(x.PeakStartMin), Num(x.PeakEndMin), Escape(x.Integration), Escape(x.Concentration) }));
    }

    // Usage: --cielene config.json vzorka.raw [vzorka2.mzML ...] --out vysledky.csv
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Pouzitie: --cielene analytes.json vzorka.raw [dalsia.mzML ...] --out vysledky.csv");
            return 2;
        }
        var configPath = Path.GetFullPath(args[0]);
        string output = "cielena_analyza.csv";
        var inputs = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) output = args[++i];
            else if (args[i].StartsWith("--")) throw new ArgumentException("Neznamy parameter: " + args[i]);
            else inputs.Add(args[i]);
        }
        if (inputs.Count == 0) throw new ArgumentException("Chyba vstupne meranie.");
        var config = JsonSerializer.Deserialize<Konfiguracia>(File.ReadAllText(configPath)) ?? throw new InvalidDataException("Neplatna konfiguracia.");
        var analytes = config.Analytes.Select(a => (a, reference: ReadReference(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, a.ReferenceFile))))).ToList();
        var rows = new List<Riadok>();
        foreach (string input in inputs)
        {
            Meranie? m = null;
            string? err = null;
            try { m = Meranie.Nacitaj(input, Path.GetFileName(input)); }
            catch (Exception e) { err = e.Message; }
            foreach (var (a, reference) in analytes)
            {
                Riadok r;
                if (m == null) r = new Riadok { File = Path.GetFileName(input), Analyte = a.Name, Reason = err ?? "Chyba nacitania" };
                else try { r = Screen(m, a, reference); }
                catch (Exception e) { r = new Riadok { File = m.Nazov, Analyte = a.Name, Reason = "Chyba vyhodnotenia: " + e.Message }; }
                rows.Add(r);
                Console.WriteLine($"{r.File}: {r.Analyte}: {r.Status} RT={Num(r.RtMin)} area={Num(r.AreaQuant)}");
            }
        }
        WriteCsv(output, rows);
        Console.WriteLine("CSV: " + Path.GetFullPath(output));
        return 0;
    }
}
