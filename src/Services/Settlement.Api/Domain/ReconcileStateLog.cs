using System.Collections.Concurrent;

namespace PandaPocket.Services.Settlement.Domain;

/// <summary>
/// Remembers the mismatch last reported for each merchant, so that the
/// reconciliation check publishes a change rather than an observation.
///
/// Why this exists. The check runs on every read of the balance, and the
/// dashboard polls that read. A merchant whose books are out by a fixed amount
/// therefore produced one CRITICAL event every few seconds for as long as
/// anybody had the page open, which buried the rest of the evidence under
/// hundreds of copies of a single fact. It was the same mistake
/// ServiceHealthPoller was written to avoid: a healthy service would have
/// produced 360 identical events an hour, so that poller records transitions
/// only, and this now does the same.
///
/// What still gets published. The first time a gap appears, and any time its
/// size changes, because a gap that is growing is a different finding from a
/// gap that is sitting still.
///
/// A gap that closes publishes nothing, and that is a deliberate gap rather
/// than an oversight. There is no catalogue type for a recovery, and inventing
/// a bare string for one is the precise drift the catalogue exists to stop: a
/// type no rule filters on and no graph node ever matches, which is how
/// LEDGER_MISMATCH came to be replaced. The state is cleared, so if the books
/// part again the next mismatch is reported as the new event it is.
///
/// Held in memory on purpose. It is a de-duplication window, not a record: the
/// events themselves are the record, and they live in the SOC service where
/// this service cannot reach them. Losing it on restart costs one repeated
/// event per affected merchant, which is the right way to fail, because a
/// restart is exactly when somebody should be told the books still do not add
/// up.
/// </summary>
public sealed class ReconcileStateLog
{
    private readonly ConcurrentDictionary<Guid, decimal> _lastReported = new();

    /// <summary>
    /// True when this difference is worth publishing: the first one seen for
    /// the merchant, or a different figure from the one last published.
    /// </summary>
    public bool ShouldReport(Guid merchantId, decimal difference)
    {
        var changed = true;

        _lastReported.AddOrUpdate(
            merchantId,
            difference,
            (_, previous) =>
            {
                changed = previous != difference;
                return difference;
            });

        return changed;
    }

    /// <summary>
    /// Clears the merchant's remembered gap and says whether one was being
    /// tracked, which is what makes a recovery reportable exactly once.
    /// </summary>
    public bool Clear(Guid merchantId) => _lastReported.TryRemove(merchantId, out _);
}
