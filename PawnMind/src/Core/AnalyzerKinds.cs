namespace PawnMind.Core
{
    public enum AnalyzerKind
    {
        None,
        Water,
        Chem,
        Plant,
        Gene
    }

    public static class AnalyzerKinds
    {
        public static AnalyzerKind FromIdentifier(string identifier)
        {
            switch ((identifier ?? string.Empty).ToLowerInvariant())
            {
                case "aquascan":
                case "water_test_strip":
                    return AnalyzerKind.Water;
                case "chem_scanner":
                    return AnalyzerKind.Chem;
                case "plant_analyser":
                    return AnalyzerKind.Plant;
                case "gene_scanner":
                    return AnalyzerKind.Gene;
                default:
                    return AnalyzerKind.None;
            }
        }
    }
}
