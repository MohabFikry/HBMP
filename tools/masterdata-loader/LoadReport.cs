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

    /// <summary>
    /// Findings that are not skips but must not be silent — an unmatched code, a column the source could
    /// not fill. A load that quietly drops data reads as a clean load, which is how a drug ends up
    /// permanently reporting "not checked" with nobody aware of it.
    /// </summary>
    public List<string> Notes { get; } = [];

    public void Skip(string reason)
    {
        Skipped++;
        SkipReasons[reason] = SkipReasons.GetValueOrDefault(reason) + 1;
    }

    public void Note(string message) => Notes.Add(message);

    public override string ToString()
    {
        var reasons = SkipReasons.Count == 0 ? "" :
            " | skips: " + string.Join(", ", SkipReasons.Select(kv => $"{kv.Key}={kv.Value}"));
        var notes = Notes.Count == 0 ? "" :
            "\n" + string.Join("\n", Notes.Select(n => $"{"",17}! {n}"));
        return $"[{Dataset,-14}] read={Read,7}  inserted={Inserted,7}  updated={Updated,7}  " +
               $"skipped={Skipped,6}  final={FinalCount,7}{reasons}{notes}";
    }
}
