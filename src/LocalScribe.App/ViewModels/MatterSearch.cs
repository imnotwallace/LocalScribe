// src/LocalScribe.App/ViewModels/MatterSearch.cs
using LocalScribe.Core.Model;

namespace LocalScribe.App.ViewModels;

/// <summary>Stage 5.4 5.3 roll-out: the app's Contains(OrdinalIgnoreCase) idiom over a matter's
/// three searchable fields (Name + Reference + Id), shared by every surface that filters
/// matters by free-text query (the matter picker, the Matters manager list, ...).</summary>
internal static class MatterSearch
{
    public static bool Matches(MattersIndexEntry e, string query)
        => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (e.Reference?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
           || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
}
