using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class AnalyzerKindTests
    {
        [Test]
        public static void Maps_Known_Analyzers()
        {
            Assert.Equal(AnalyzerKind.Water, AnalyzerKinds.FromIdentifier("aquascan"), "aquascan");
            Assert.Equal(AnalyzerKind.Water, AnalyzerKinds.FromIdentifier("water_test_strip"), "strip");
            Assert.Equal(AnalyzerKind.Chem, AnalyzerKinds.FromIdentifier("chem_scanner"), "chem");
            Assert.Equal(AnalyzerKind.Plant, AnalyzerKinds.FromIdentifier("plant_analyser"), "plant");
            Assert.Equal(AnalyzerKind.Gene, AnalyzerKinds.FromIdentifier("gene_scanner"), "gene");
            Assert.Equal(AnalyzerKind.None, AnalyzerKinds.FromIdentifier("screwdriver"), "other");
        }
    }
}
