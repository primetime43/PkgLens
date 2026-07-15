using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class CliContractTests
{
    private static string CliPath => Path.Combine(AppContext.BaseDirectory, "pkglens.dll");

    public static IEnumerable<object[]> JsonCommandArguments =>
    [
        [new[] { "info", "--json" }],
        [new[] { "list", "--json" }],
        [new[] { "sfo", "--json" }],
        [new[] { "verify", "--json" }],
        [new[] { "extract", "--json" }],
        [new[] { "decrypt", "--json" }],
        [new[] { "self", "--json" }],
        [new[] { "folderinfo", "--json" }],
        [new[] { "scan", "--json" }],
        [new[] { "pack", "--json" }],
        [new[] { "resign", "--json" }],
        [new[] { "unself", "--json" }],
        [new[] { "patch", "--json" }],
        [new[] { "unpbp", "--json" }],
        [new[] { "undoc", "--json" }],
        [new[] { "psar", "info", "--json" }],
        [new[] { "keys", "status", "--json" }],
    ];

    [Fact]
    public async Task Version_MatchesInformationalVersion()
    {
        CommandResult result = await RunAsync("--version");
        string version = Assembly.LoadFrom(CliPath)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"pkglens {version}", result.StandardOutput.Trim());
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task SelfJson_IsOneParseableJsonDocument()
    {
        string directory = CreateTempDirectory();
        try
        {
            string input = Path.Combine(directory, "EBOOT.BIN");
            await File.WriteAllBytesAsync(input, new SyntheticSelfBuilder().Build());

            CommandResult result = await RunAsync("self", input, "--json");
            using JsonDocument json = JsonDocument.Parse(result.StandardOutput);

            Assert.Equal(0, result.ExitCode);
            Assert.True(json.RootElement.GetProperty("npdrm").GetBoolean());
            Assert.Empty(result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractJson_ContainsStructuredResultWithoutProse()
    {
        string directory = CreateTempDirectory();
        try
        {
            string input = Path.Combine(directory, "sample.pkg");
            string output = Path.Combine(directory, "extracted");
            await File.WriteAllBytesAsync(input, new SyntheticPkgBuilder().AddFile("USRDIR/test.bin", "payload").Build());

            CommandResult result = await RunAsync("extract", input, "--out", output, "--json");
            using JsonDocument json = JsonDocument.Parse(result.StandardOutput);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("extract", json.RootElement.GetProperty("command").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("fileCount").GetInt32());
            Assert.Equal("USRDIR/test.bin", json.RootElement.GetProperty("files")[0].GetString());
            Assert.Empty(result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResignJson_ContainsStructuredMutationResult()
    {
        string directory = CreateTempDirectory();
        try
        {
            string input = Path.Combine(directory, "input.elf");
            string output = Path.Combine(directory, "output.self");
            await File.WriteAllBytesAsync(input, MinimalElf.Build());

            CommandResult result = await RunAsync("resign", input, "--out", output, "--json");
            using JsonDocument json = JsonDocument.Parse(result.StandardOutput);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("resign", json.RootElement.GetProperty("command").GetString());
            Assert.True(json.RootElement.GetProperty("fakeSigned").GetBoolean());
            Assert.True(File.Exists(output));
            Assert.Empty(result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(JsonCommandArguments))]
    public async Task EveryCommandRecognizesJson(string[] arguments)
    {
        CommandResult result = await RunAsync(arguments);

        Assert.DoesNotContain("unknown option '--json'", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CommandResult> RunAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(CliPath);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pkglens-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
