using System.ComponentModel;
using System.Windows.Controls;

namespace Purge.UI;

/// <summary>
/// GridViewの列幅をListViewの実際の幅に合わせて比例配分するヘルパー。
///
/// WPFのGridViewColumnにはWidthのStar指定(残り幅をすべて使う)機能が無く、固定ピクセル幅しか
/// 指定できない。そのためウィンドウを縮めると列が見切れ、広げると右側に空白が残る。
/// ここではListViewのSizeChangedに追従して各列の実幅を再計算する。
///
/// GridViewColumnはFrameworkElementではなくTagプロパティを持たないため、比率はXAMLではなく
/// 呼び出し側(コードビハインド)から列インデックス順に渡す。
///
/// ユーザーが列ヘッダーの境界をドラッグして手動リサイズした場合、その列は以後「固定幅」
/// として扱い、比率配分の対象から外す。そうしないと次のウィンドウリサイズで手動調整が
/// 上書きされて元に戻ってしまう(Issue #28で指摘された不具合)。
/// </summary>
public static class GridViewColumnSizer
{
    private const double ReservedWidth = 28;
    private const double MinColumnWidth = 60;

    /// <summary>
    /// ListViewのSizeChangedに購読し、列幅をウィンドウ幅へ自動追従させる。
    /// 併せて、ユーザーの手動リサイズを検知して以後その列を比率配分から除外する。
    /// </summary>
    /// <param name="listView">対象のListView(ViewはGridViewであること)。</param>
    /// <param name="ratios">
    /// 列インデックス順の伸縮比率。null(またはNaN)を指定した列は固定幅として扱い、
    /// 比率配分の対象から除外する(アイコン列など)。
    /// 配列長が列数より短い場合、残りの列は固定幅扱いになる。
    /// </param>
    public static void AttachAutoSize(ListView listView, double?[] ratios)
    {
        // 呼び出し元の配列を直接書き換えないよう複製する。ユーザーが手動リサイズした列は
        // ここでnullに書き換えて「以後は固定幅」として扱う。
        var mutableRatios = (double?[])ratios.Clone();
        bool isApplyingProgrammatically = false;

        if (listView.View is GridView gridView)
        {
            for (int i = 0; i < gridView.Columns.Count; i++)
            {
                if (i >= mutableRatios.Length || !mutableRatios[i].HasValue) continue;

                int columnIndex = i; // クロージャ用にキャプチャ
                var column = gridView.Columns[columnIndex];

                // GridViewColumn.WidthはDependencyPropertyではないため、
                // DependencyPropertyDescriptorで変更を監視する。
                var descriptor = DependencyPropertyDescriptor.FromProperty(
                    GridViewColumn.WidthProperty, typeof(GridViewColumn));
                descriptor?.AddValueChanged(column, (_, _) =>
                {
                    // Apply()自身によるWidth変更は無視する(自分の処理で自分を固定幅化しない)。
                    if (isApplyingProgrammatically) return;

                    // ユーザーがヘッダー境界をドラッグした結果の変更とみなし、
                    // 以後この列は比率配分の対象から外す(固定幅として尊重する)。
                    mutableRatios[columnIndex] = null;
                });
            }
        }

        listView.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;

            isApplyingProgrammatically = true;
            try
            {
                Apply(listView, mutableRatios);
            }
            finally
            {
                isApplyingProgrammatically = false;
            }
        };
    }

    /// <summary>
    /// 列幅を再配分する。
    /// </summary>
    public static void Apply(ListView listView, double?[] ratios)
    {
        if (listView.View is not GridView gridView) return;

        double available = listView.ActualWidth - ReservedWidth;
        if (available <= 0) return;

        double fixedTotal = 0;
        double ratioTotal = 0;
        int ratioColumnCount = 0;

        for (int i = 0; i < gridView.Columns.Count; i++)
        {
            double? ratio = i < ratios.Length ? ratios[i] : null;
            if (ratio.HasValue)
            {
                ratioTotal += ratio.Value;
                ratioColumnCount++;
            }
            else
            {
                fixedTotal += gridView.Columns[i].ActualWidth;
            }
        }

        if (ratioTotal <= 0) return;

        double distributable = available - fixedTotal;

        double minRequired = MinColumnWidth * ratioColumnCount;
        if (distributable < minRequired) distributable = minRequired;

        for (int i = 0; i < gridView.Columns.Count; i++)
        {
            double? ratio = i < ratios.Length ? ratios[i] : null;
            if (ratio.HasValue)
            {
                gridView.Columns[i].Width = distributable * (ratio.Value / ratioTotal);
            }
        }
    }
}
