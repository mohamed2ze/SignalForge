namespace SignalForge.Worker.Services;

/// <summary>
/// Selects a claim batch with per-tenant fairness. Both worker loops scan a larger window
/// (<c>BatchSize × ScanMultiplier</c>) of globally oldest candidates, then this helper picks the
/// <c>batchSize</c> fair subset. Straight global oldest-first ordering would let one
/// tenant's flood fill every batch and indefinitely park another tenant's work; the round-robin
/// (one candidate per tenant per round, tenants ordered by their oldest candidate) bounds the
/// share a single tenant can take per cycle.
/// </summary>
internal static class FairBatchSelector
{
    public static List<Guid> PickFairBatch(
        IReadOnlyList<(Guid Id, Guid TenantId)> scanned,
        int batchSize)
    {
        var result = new List<Guid>(Math.Min(batchSize, scanned.Count));
        if (scanned.Count == 0 || batchSize <= 0)
            return result;

        // `scanned` arrives oldest-first, so each tenant's first occurrence is its oldest item.
        // Order tenants by that oldest item so quieter tenants are not simply appended last.
        var tenantQueues = scanned
            .Select((item, index) => (Item: item, Index: index))
            .GroupBy(x => x.Item.TenantId)
            .Select(g => new
            {
                FirstIndex = g.Min(x => x.Index),
                Items = g.OrderBy(x => x.Index).Select(x => x.Item).ToList()
            })
            .OrderBy(q => q.FirstIndex)
            .ToList();

        for (var round = 0; result.Count < batchSize; round++)
        {
            var advancedAny = false;
            foreach (var queue in tenantQueues)
            {
                if (result.Count >= batchSize)
                    break;

                if (round < queue.Items.Count)
                {
                    result.Add(queue.Items[round].Id);
                    advancedAny = true;
                }
            }

            // A round with no progress means every tenant's queue is exhausted.
            if (!advancedAny)
                break;
        }

        return result;
    }
}