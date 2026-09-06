using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// A recycle bin that just deletes, so the suite does not fill the developer's actual
/// bin with hundreds of test files. It records what it was asked to delete, which is how
/// the "delete is recoverable" behaviour is asserted without inspecting the shell.
/// </summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    public List<string> Deleted { get; } = new();

    public bool Delete(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Deleted.Add(path);
        File.Delete(path);
        return true;
    }
}

public sealed class StartScriptStoreTests
{
    private const string SampleScript = "[game]\r\n{\r\nmapname=Quicksilver Remake 1.24;\r\n}\r\n";

    private static StartScriptStore CreateStore(TempDirectory temp, IRecycleBin? recycleBin = null) =>
        new(new TestLogger<StartScriptStore>(), recycleBin ?? new FakeRecycleBin(),
            Path.Combine(temp.Path, "StartScripts"));

    // PLAN.md §9 Q4: the library starts empty; the old app's folder is not imported.
    [Fact]
    public async Task ListAsync_ReturnsNothingForAFreshLibrary()
    {
        using var temp = new TempDirectory();

        Assert.Empty(await CreateStore(temp).ListAsync());
    }

    [Fact]
    public void ResolvePath_AddsTheExtensionWhenMissing()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        Assert.Equal(Path.Combine(store.LibraryPath, "vs_ai.txt"), store.ResolvePath("vs_ai"));
        Assert.Equal(Path.Combine(store.LibraryPath, "vs_ai.txt"), store.ResolvePath("vs_ai.txt"));
    }

    /// <summary>
    /// The library is flat, so a stored name carrying a path must not be able to escape
    /// it and point the engine at an arbitrary file.
    /// </summary>
    [Fact]
    public void ResolvePath_StripsAnyDirectoryPart()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        string resolved = store.ResolvePath(@"..\..\Windows\System32\evil.txt");

        Assert.Equal(Path.Combine(store.LibraryPath, "evil.txt"), resolved);
    }

    // ---- create / read / save -------------------------------------------------

    [Fact]
    public async Task CreateAsync_WritesTheScriptAndListsIt()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        StartScriptFile? created = await store.CreateAsync("vs_ai", SampleScript);

        Assert.NotNull(created);
        Assert.Equal("vs_ai.txt", created!.FileName);
        Assert.Equal(SampleScript, await store.ReadTextAsync("vs_ai"));
        Assert.Equal("vs_ai", Assert.Single(await store.ListAsync()).DisplayName);
    }

    /// <summary>Creating twice must never silently overwrite the first script.</summary>
    [Fact]
    public async Task CreateAsync_UniquesAgainstAnExistingName()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        await store.CreateAsync("vs_ai", SampleScript);
        StartScriptFile? second = await store.CreateAsync("vs_ai", "[game]\r\n{\r\n}\r\n");
        StartScriptFile? third = await store.CreateAsync("vs_ai", "[game]\r\n{\r\n}\r\n");

        Assert.Equal("vs_ai 2.txt", second!.FileName);
        Assert.Equal("vs_ai 3.txt", third!.FileName);

        // The original is untouched.
        Assert.Equal(SampleScript, await store.ReadTextAsync("vs_ai"));
    }

    [Fact]
    public async Task CreateAsync_RefusesAnInvalidName()
    {
        using var temp = new TempDirectory();

        Assert.Null(await CreateStore(temp).CreateAsync("bad:name", SampleScript));
    }

    [Fact]
    public async Task SaveTextAsync_OverwritesInPlaceAndLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);

        await store.SaveTextAsync("vs_ai", "[game]\r\n{\r\nmapname=Otago 1.43;\r\n}\r\n");

        Assert.Contains("Otago", await store.ReadTextAsync("vs_ai")!);
        Assert.Single(await store.ListAsync());
        Assert.Empty(Directory.GetFiles(store.LibraryPath, "*.tmp"));
    }

    /// <summary>
    /// The engine reads the script as bytes and a BOM would corrupt the first token, so
    /// the file must start with '[' and nothing else.
    /// </summary>
    [Fact]
    public async Task SaveTextAsync_WritesUtf8WithoutAByteOrderMark()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        StartScriptFile? file = await store.SaveTextAsync("vs_ai", SampleScript);

        byte[] bytes = await File.ReadAllBytesAsync(file!.FullPath);

        Assert.Equal((byte)'[', bytes[0]);
    }

    [Fact]
    public async Task ReadTextAsync_ReturnsNullForAScriptThatIsNotThere()
    {
        using var temp = new TempDirectory();

        Assert.Null(await CreateStore(temp).ReadTextAsync("nope"));
    }

    /// <summary>The engine holds _script.txt open while a match runs.</summary>
    [Fact]
    public async Task ReadTextAsync_ReadsAScriptThatIsOpenForWritingElsewhere()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        StartScriptFile? file = await store.CreateAsync("vs_ai", SampleScript);

        await using var holder = new FileStream(
            file!.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal(SampleScript, await store.ReadTextAsync("vs_ai"));
    }

    // ---- duplicate ------------------------------------------------------------

    [Fact]
    public async Task DuplicateAsync_CopiesTheContentUnderACopyName()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);

        StartScriptFile? copy = await store.DuplicateAsync("vs_ai");

        Assert.Equal("vs_ai copy.txt", copy!.FileName);
        Assert.Equal(SampleScript, await store.ReadTextAsync("vs_ai copy"));
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task DuplicateAsync_UniquesRepeatedCopies()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);

        await store.DuplicateAsync("vs_ai");
        StartScriptFile? second = await store.DuplicateAsync("vs_ai");

        Assert.Equal("vs_ai copy 2.txt", second!.FileName);
    }

    [Fact]
    public async Task DuplicateAsync_ReturnsNullForAMissingScript()
    {
        using var temp = new TempDirectory();

        Assert.Null(await CreateStore(temp).DuplicateAsync("nope"));
    }

    // ---- rename ---------------------------------------------------------------

    [Fact]
    public async Task RenameAsync_MovesTheScriptAndKeepsItsContent()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);

        StartScriptFile? renamed = await store.RenameAsync("vs_ai", "1v1 versus BARb");

        Assert.Equal("1v1 versus BARb.txt", renamed!.FileName);
        Assert.Equal(SampleScript, await store.ReadTextAsync("1v1 versus BARb"));
        Assert.Single(await store.ListAsync());
    }

    /// <summary>A typo that matches another script must not destroy it.</summary>
    [Fact]
    public async Task RenameAsync_RefusesToOverwriteAnotherScript()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);
        await store.CreateAsync("ai_vs_ai", "[game]\r\n{\r\n}\r\n");

        Assert.Null(await store.RenameAsync("vs_ai", "ai_vs_ai"));

        // Both survive, and the target keeps its own content.
        Assert.Equal(2, (await store.ListAsync()).Count);
        Assert.DoesNotContain("Quicksilver", await store.ReadTextAsync("ai_vs_ai")!);
    }

    /// <summary>
    /// Windows treats these as the same file, so File.Move would call the destination
    /// "already existing" and a pure case change would be impossible.
    /// </summary>
    [Fact]
    public async Task RenameAsync_AllowsAChangeOfCaseOnly()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);

        StartScriptFile? renamed = await store.RenameAsync("vs_ai", "VS_AI");

        Assert.NotNull(renamed);
        Assert.Equal("VS_AI.txt", renamed!.FileName);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task RenameAsync_RefusesAnInvalidName()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);
        await store.CreateAsync("vs_ai", SampleScript);

        Assert.Null(await store.RenameAsync("vs_ai", "  "));
        Assert.Null(await store.RenameAsync("vs_ai", @"sub\folder"));
        Assert.Single(await store.ListAsync());
    }

    // ---- delete ---------------------------------------------------------------

    /// <summary>PLAN.md §5.7 asks for delete-to-recycle-bin, so a mis-click is recoverable.</summary>
    [Fact]
    public async Task Delete_SendsTheScriptToTheRecycleBinRatherThanDestroyingIt()
    {
        using var temp = new TempDirectory();
        var bin = new FakeRecycleBin();
        StartScriptStore store = CreateStore(temp, bin);
        StartScriptFile? file = await store.CreateAsync("vs_ai", SampleScript);

        Assert.True(store.Delete("vs_ai"));

        Assert.Equal(file!.FullPath, Assert.Single(bin.Deleted));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public void Delete_ReturnsFalseForAMissingScript()
    {
        using var temp = new TempDirectory();

        Assert.False(CreateStore(temp).Delete("nope"));
    }

    // ---- names ----------------------------------------------------------------

    [Theory]
    [InlineData("vs_ai")]
    [InlineData("1v1 versus BARb")]
    [InlineData("godless_vs_barb.txt")]
    public void ValidateName_AcceptsRealisticNames(string name)
    {
        using var temp = new TempDirectory();

        Assert.True(CreateStore(temp).ValidateName(name).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad:name")]
    [InlineData("bad/name")]
    [InlineData(@"bad\name")]
    [InlineData("trailing.")]
    [InlineData(".txt")]
    [InlineData("CON")]
    [InlineData("lpt1")]
    public void ValidateName_RejectsNamesWindowsCannotStoreFaithfully(string? name)
    {
        using var temp = new TempDirectory();

        ScriptNameValidation result = CreateStore(temp).ValidateName(name);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task MakeUniqueName_LeavesAFreeNameAlone()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        Assert.Equal("vs_ai", store.MakeUniqueName("vs_ai"));

        await store.CreateAsync("vs_ai", SampleScript);

        Assert.Equal("vs_ai 2", store.MakeUniqueName("vs_ai"));
    }

    // ---- import ---------------------------------------------------------------

    [Fact]
    public async Task ImportAsync_CopiesAFileIntoTheLibrary()
    {
        using var temp = new TempDirectory();
        string source = Path.Combine(temp.Path, "_script.txt");
        await File.WriteAllTextAsync(source, SampleScript);

        StartScriptStore store = CreateStore(temp);
        StartScriptFile? imported = await store.ImportAsync(source, "from-chobby");

        Assert.NotNull(imported);
        Assert.Equal("from-chobby.txt", imported!.FileName);
        Assert.Contains("Quicksilver", await File.ReadAllTextAsync(imported.FullPath));
        Assert.Equal("from-chobby", Assert.Single(await store.ListAsync()).DisplayName);
    }

    /// <summary>
    /// WinUI's TextBox returns a bare CR for every line the user typed, so a script saved
    /// straight from the raw editor arrives with mixed line endings. Left alone it lands
    /// on disk as one enormous line — this was found in a real saved script.
    /// </summary>
    [Fact]
    public async Task SaveTextAsync_NormalisesEveryLineEndingToCrLf()
    {
        using var temp = new TempDirectory();
        StartScriptStore store = CreateStore(temp);

        // As the editor would hand it over: CR from typing, CRLF from the serializer.
        await store.CreateAsync("mixed", "[game]\r{\r\n\tishost=1;\r}\n");

        string text = await File.ReadAllTextAsync(Path.Combine(store.LibraryPath, "mixed.txt"));

        Assert.DoesNotContain('\r', text.Replace("\r\n", string.Empty));
        Assert.Equal(4, text.Split("\r\n").Length - 1);
    }
}
