namespace YeniRPA.Web.Services.SalesAnalysis.Country;

/// <summary>The two parsed exports of a country comparison and the names the operator uploaded them under.</summary>
public sealed record CountryPair(SalesDataset A, string FileA, SalesDataset B, string FileB);

/// <summary>
/// Holds the most recently loaded pair of orders exports for the country comparison.
///
/// <para>In memory only, like <see cref="SalesAnalysisStore"/>: the parsed lines are worthless after a
/// restart (the operator re-uploads), so they are deliberately not written to LiteDB. It is a separate
/// store so that loading two countries never drops the dataset the period analysis is working on. One
/// pair is kept; a token naming a replaced pair is refused rather than answered from stale data.</para>
/// </summary>
public sealed class CountryComparisonStore
{
    readonly object _sync = new();
    (string Token, CountryPair Pair)? _current;

    public string Put(CountryPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var token = Guid.NewGuid().ToString("N");
        lock (_sync)
            _current = (token, pair);
        return token;
    }

    public CountryPair? Get(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        lock (_sync)
            return _current is { } current && current.Token == token.Trim() ? current.Pair : null;
    }
}
