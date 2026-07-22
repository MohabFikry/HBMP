namespace Mersal.MasterData.Loader;

/// <summary>A per-dataset load report: rows read/inserted/updated/skipped + reasons + final count.</summary>
public sealed class LoadReport(string dataset)
{
    public string Dataset { get; } = dataset;
    public int Read { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int FinalCount { get; set; }
    public Dictionary<string, int> SkipReasons { get; } = new(StringComparer.Ordinal);

    public void Skip(string reason)
    {
        Skipped++;
        SkipReasons[reason] = SkipReasons.GetValueOrDefault(reason) + 1;
    }

    public override string ToString()
    {
        var reasons = SkipReasons.Count == 0 ? "" :
            " | skips: " + string.Join(", ", SkipReasons.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"[{Dataset,-14}] read={Read,7}  inserted={Inserted,7}  updated={Updated,7}  " +
               $"skipped={Skipped,6}  final={FinalCount,7}{reasons}";
    }
}
