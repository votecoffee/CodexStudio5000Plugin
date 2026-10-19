using Studio5000.Core;

namespace Studio5000.Desktop;

internal sealed record SavedReviewChoice(ReviewSummary Summary)
{
    public override string ToString() => $"{Summary.Name} · r{Summary.Revision}";
}
