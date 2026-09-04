using UninstallTool;

namespace UninstallTool.Tests;

/// <summary>
/// OperationLog の自動テスト。
/// ログの記録・取得・容量制限・エラーレポート生成を確認する。
/// </summary>
public class OperationLogTests
{
    [Fact]
    public void Infoを記録できる()
    {
        var log = new OperationLog();
        log.Info("TestCategory", "テスト説明", "詳細情報");

        var steps = log.GetRecent(10);
        Assert.Single(steps);
        Assert.Equal("TestCategory", steps[0].Category);
        Assert.Equal("テスト説明", steps[0].Description);
        Assert.Equal("詳細情報", steps[0].Detail);
        Assert.Equal(OperationStepLevel.Info, steps[0].Level);
    }

    [Fact]
    public void Warningを記録できる()
    {
        var log = new OperationLog();
        log.Warning("TestCategory", "警告説明");

        var steps = log.GetRecent(10);
        Assert.Single(steps);
        Assert.Equal(OperationStepLevel.Warning, steps[0].Level);
    }

    [Fact]
    public void Errorを記録できる()
    {
        var log = new OperationLog();
        log.Error("TestCategory", "エラー説明");

        var steps = log.GetRecent(10);
        Assert.Single(steps);
        Assert.Equal(OperationStepLevel.Error, steps[0].Level);
    }

    [Fact]
    public void 詳細なしでもInfoを記録できる()
    {
        var log = new OperationLog();
        log.Info("TestCategory", "説明のみ");

        var steps = log.GetRecent(10);
        Assert.Single(steps);
        Assert.Null(steps[0].Detail);
    }

    [Fact]
    public void 複数件のログが時系列で取得できる()
    {
        var log = new OperationLog();
        log.Info("Cat", "Step1");
        log.Info("Cat", "Step2");
        log.Info("Cat", "Step3");

        var steps = log.GetRecent(10);
        Assert.Equal(3, steps.Count);
        Assert.Equal("Step1", steps[0].Description);
        Assert.Equal("Step2", steps[1].Description);
        Assert.Equal("Step3", steps[2].Description);
    }

    [Fact]
    public void GetRecentでn件を取得できる()
    {
        var log = new OperationLog();
        for (int i = 0; i < 10; i++)
            log.Info("Cat", $"Step{i}");

        var steps = log.GetRecent(3);
        Assert.Equal(3, steps.Count);
        // 最新3件が返ることを確認
        Assert.Equal("Step7", steps[0].Description);
        Assert.Equal("Step8", steps[1].Description);
        Assert.Equal("Step9", steps[2].Description);
    }

    [Fact]
    public void 容量を超えた場合古いログが消える()
    {
        var log = new OperationLog(capacity: 3);
        log.Info("Cat", "Step0");
        log.Info("Cat", "Step1");
        log.Info("Cat", "Step2");
        log.Info("Cat", "Step3"); // capacity=3 なので Step0 が消えるはず

        var steps = log.GetRecent(10);
        Assert.Equal(3, steps.Count);
        Assert.Equal("Step1", steps[0].Description);
        Assert.Equal("Step3", steps[2].Description);
    }

    [Fact]
    public void Clearで全件消える()
    {
        var log = new OperationLog();
        log.Info("Cat", "Step1");
        log.Info("Cat", "Step2");
        log.Clear();

        var steps = log.GetRecent(10);
        Assert.Empty(steps);
    }

    [Fact]
    public void BuildErrorReportにエラー情報が含まれる()
    {
        var log = new OperationLog();
        log.Info("TestCategory", "事前操作");

        var ex = new InvalidOperationException("テスト例外メッセージ");
        var report = log.BuildErrorReport(ex);

        Assert.Contains("テスト例外メッセージ", report);
        Assert.Contains("InvalidOperationException", report);
        Assert.Contains("事前操作", report);
        Assert.Contains("## エラー報告", report);
    }

    [Fact]
    public void BuildErrorReportでログゼロでもクラッシュしない()
    {
        var log = new OperationLog();
        var ex = new Exception("ログなし例外");
        var report = log.BuildErrorReport(ex);

        Assert.Contains("ログなし例外", report);
        Assert.Contains("記録された操作ログがありません", report);
    }
}
