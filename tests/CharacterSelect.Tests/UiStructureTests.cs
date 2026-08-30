using System;
using System.IO;
using Xunit;

namespace CharacterSelect.Tests;

public class ScreenStructureTests {
    private static string ReadRml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "screens", "CharSelect.rml"));

    private static string ReadPlugin() =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CharacterSelect", "CharacterSelectPlugin.cs")));

    [Fact]
    public void PopulationBoxRendersNameAndPopulationOnSeparateLines() {
        var rml = ReadRml();

        // Two-line world box: name first, then population label.
        Assert.Contains("class = \"world-name\"", rml);
        Assert.Contains("Population: \" .. state.CurrentConnectionCount", rml);
        Assert.Matches(@"\.world-name \{[^}]*display: block", rml);
        // The single-line format string from the stock screen is gone.
        Assert.DoesNotContain("%s %d / %d", ReadRml());
    }

    [Fact]
    public void CharacterRowsShowNameLevelAndAllegiance() {
        var rml = ReadRml();

        // Name left, large level right, allegiance underneath in angle brackets.
        Assert.Contains("class = \"char-name\"", rml);
        Assert.Contains("char-allegiance", rml);
        Assert.Matches(@"\.char-level \{[^}]*font-size: 20px", rml);
        Assert.Matches(@"\.char-level \{[^}]*right: 8px", rml);
        // Angle-bracket allegiance rendering.
        Assert.Contains("\"<\" .. char.Allegiance .. \">\"", rml);
    }

    [Fact]
    public void PluginPersistsCapturedFactsAndRegistersScreen() {
        var cs = ReadCSharp();

        // Screen override attempt is logged with its result.
        Assert.Contains("RegisterScreen(\"CharSelect\"", cs);
        // Capture subscribes to the AC plugin's player-description event.
        Assert.Contains("OnLogin_PlayerDescription", cs);
        // Property ids: Level = PropertyInt 25, AllegianceName = PropertyString 47.
        Assert.Contains("== 25", cs);
        Assert.Contains("== 47", cs);
        // Store persists to characters.json.
        Assert.Contains("characters.json", File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CharacterSelect", "CharacterStore.cs")));
    }

    private static string ReadCSharp() =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CharacterSelect", "CharacterSelectPlugin.cs")));
}
