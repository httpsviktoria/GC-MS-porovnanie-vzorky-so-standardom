namespace GcmsPorovnanie;

// Deterministic synthetic smoke tests, not validation against real GC-MS samples.
public static class CielenaAnalyzaTest
{
    static Meranie Synthetic(double sigmaSeconds, bool flat = false)
    {
        const int n = 801, mzMin = 50, mzMax = 250;
        var rt = Enumerable.Range(0, n).Select(i => 700.0 + 0.5 * i).ToArray();
        var sp = new double[n][];
        int[] ions = { 75, 104, 117, 146, 203, 221, 236 };
        double[] amps = { 15000, 3600, 7000, 11000, 2800, 6000, 1000 };
        for (int i = 0; i < n; i++)
        {
            sp[i] = new double[mzMax - mzMin + 1];
            for (int k = 0; k < ions.Length; k++)
            {
                double signal = flat ? 0 : amps[k] * Math.Exp(-0.5 * Math.Pow((rt[i] - 903.6) / sigmaSeconds, 2));
                sp[i][ions[k] - mzMin] = 15 + signal;
            }
        }
        return new Meranie { Nazov = "synthetic", Rt = rt, Tic = new double[n], Spec = sp, MzMin = mzMin, MzMax = mzMax };
    }

    public static int Run()
    {
        var a = new CielenaAnalyza.Latka
        {
            Name = "synthetic IS", ReferenceStatus = "UNCONFIRMED",
            QuantIon = 221, QualifierIons = new[] {236, 75, 146, 117, 104, 203},
            RtMin = 14.7, RtMax = 15.5, MinCosine = 0.8
        };
        var reference = new Dictionary<int, double> {
            [75] = 15000, [104] = 3600, [117] = 7000,
            [146] = 11000, [203] = 2800, [221] = 6000, [236] = 1000
        };
        var narrow = CielenaAnalyza.Screen(Synthetic(1.4), a, reference);
        if (narrow.Status != "CANDIDATE_REVIEW" || narrow.AreaQuant is not > 0 ||
            narrow.QualifierCoelutionCorr is not > 0.95 || narrow.IntegrationClipped)
            throw new Exception("Narrow positive synthetic peak failed: " + narrow.Status + " " + narrow.Reason);
        var broad = CielenaAnalyza.Screen(Synthetic(28), a, reference);
        if (!broad.IntegrationClipped || broad.Status == "CANDIDATE_REVIEW")
            throw new Exception("Broad synthetic peak was not flagged as clipped.");
        var blank = CielenaAnalyza.Screen(Synthetic(1.4, flat:true), a, reference);
        if (blank.Status != "NOT_DETECTED_SCREEN")
            throw new Exception("Blank synthetic measurement incorrectly detected.");
        Console.WriteLine("PASS: normal peak, clipped peak, blank; synthetic smoke tests only.");
        return 0;
    }
}
