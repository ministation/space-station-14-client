namespace Port.Content;

public sealed record ContentDownloadProgress(
    string Stage,
    int Done,
    int Total,
    long BytesWritten,
    string? CurrentPath = null,
    string? Detail = null)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);
    public int Percent => (int)Math.Round(Fraction * 100);
    public string Line =>
        string.IsNullOrWhiteSpace(CurrentPath)
            ? $"{Stage}: {Done}/{Total} ({Percent}%) {Detail}".Trim()
            : $"{Stage}: {Done}/{Total} ({Percent}%) {CurrentPath} {Detail}".Trim();
}
