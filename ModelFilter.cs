using Microsoft.AI.Foundry.Local;
using System.Diagnostics.CodeAnalysis;

namespace ToolCallingScratch
{
    internal enum ModelMatchKind
    {
        Matched,
        DidntMatch,
        NotRequested,
        ExplicitlyIgnored
    }
    
    internal record class MatchInfo(string Name, IModel Model, ModelMatchKind MatchKind, string? ReasonForOmission);

    internal record class ModelFilter(List<string> SpecificModels, int MaxSizeInMb, List<string> IgnoredModels, bool TryAllVariants,
        bool IgnoreToolsSupport, string? SkipPastModel, List<string> RegisteredEps, bool AllowCpu)
    {
        internal const int DefaultMaxFileSize = 6 * 1_024; // 6 gigs; reasonable default for machines with 16 GB of RAM.

        internal static ModelFilter Default { get; } = new([], DefaultMaxFileSize, [], false, false, null, [], false);

        internal List<MatchInfo> GetAllMatches(IEnumerable<IModel> models)
        {
            bool foundSkipPast = false;
            return GetAllMatchesImpl(models, ref foundSkipPast);
        }

        List<MatchInfo> GetAllMatchesImpl(IEnumerable<IModel> models, ref bool foundSkipPast)
        {
            List<MatchInfo> results = [];
            var optionsNoVariants = this with { TryAllVariants = false };

            foreach (var model in models)
            {
                if (TryAllVariants)
                {
                    foreach (var variant in model.Variants)
                    {
                        results.AddRange(optionsNoVariants.GetAllMatchesImpl([variant], ref foundSkipPast));
                    }
                }
                else
                {
                    var matchKind = GetMatchKind(model, ref foundSkipPast, out var reason);
                    results.Add(new(model.Info.Name, model, matchKind, reason));
                }
            }

            return results;
        }

        internal ModelMatchKind GetMatchKind(IModel model, ref bool foundSkipPast, [NotNullWhen(false)] out string? reasonForOmission)
        {
            reasonForOmission = null;

            if (SkipPastModel is not null && !foundSkipPast)
            {
                if (model.Alias.Contains(SkipPastModel, StringComparison.OrdinalIgnoreCase) ||
                    model.Info.Name.Contains(SkipPastModel, StringComparison.OrdinalIgnoreCase))
                {
                    foundSkipPast = true;
                }
                reasonForOmission = $"`oModel is being skipped.";
                return ModelMatchKind.ExplicitlyIgnored;
            }
            if (SpecificModels.Count > 0 && !SpecificModels.Any(name =>
                    model.Alias.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    model.Info.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                reasonForOmission = $"`oModel doesn't match a requested name or alias.";
                return ModelMatchKind.NotRequested;
            }
            if (IgnoredModels.Any(name =>
                model.Alias.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                model.Info.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                reasonForOmission = $"`oModel is explicitly ignored.";
                return ModelMatchKind.ExplicitlyIgnored;
            }
            if (!IgnoreToolsSupport && (!model.Info.SupportsToolCalling.HasValue || model.Info.SupportsToolCalling.Value == false))
            {
                reasonForOmission = $"`oModel doesn't support tool-calling.";
                return ModelMatchKind.DidntMatch;
            }
            if (model.Info.FileSizeMb > MaxSizeInMb)
            {
                reasonForOmission = $"`oModel is too large ({model.Info.FileSizeMb:N0} MB).";
                return ModelMatchKind.DidntMatch;
            }
            if (!AllowCpu && model.Info.Runtime!.DeviceType is DeviceType.CPU)
            {
                reasonForOmission = $"`oModel only runs on the CPU.";
                return ModelMatchKind.DidntMatch;
            }
            if (RegisteredEps.Count > 0 && !RegisteredEps.Contains(model.Info.Runtime!.ExecutionProvider))
            {
                reasonForOmission = $"`oModel is not supported by any registered EP.";
                return ModelMatchKind.DidntMatch;
            }

            return ModelMatchKind.Matched;
        }
    }
}
