using WhisperBatch.Core;

namespace WhisperBatch.Core.Tests;

/// <summary>テストでは、リポジトリの Localization フォルダから文字列を読み込み、日本語表示にそろえる</summary>
public static class TestLocalization
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Directory => Path.Combine(RepoRoot, "Localization");

    private static readonly Lazy<bool> Loaded = new(() =>
    {
        Localizer.Shared.LoadTables(Directory);
        Localizer.Shared.SetLanguage(AppLanguage.Japanese);
        return true;
    });

    public static void SetUp() => _ = Loaded.Value;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WhisperBatch.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリのルートが見つかりません");
    }
}
