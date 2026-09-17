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
/// </summary>
public static class GridViewColumnSizer
{
    /// <summary>
    /// 縦スクロールバーとボーダーの分。これを差し引かないと列合計が実幅を超え、
    /// 常に横スクロールバーが出てしまう。
    /// </summary>
    private const double ReservedWidth = 28;

    /// <summary>
    /// 狭くしすぎると列が潰れて読めなくなるため、比率配分する列の下限幅。
    /// 合計がListView幅を超える場合は横スクロールに委ねる。
    /// </summary>
    private const double MinColumnWidth = 60;

    /// <summary>
    /// ListViewのSizeChangedに購読し、列幅をウィンドウ幅へ自動追従させる。
    /// </summary>
    /// <param name="listView">対象のListView(ViewはGridViewであること)。</param>
    /// <param name="ratios">
    /// 列インデックス順の伸縮比率。null(またはNaN)を指定した列は固定幅として扱い、
    /// 比率配分の対象から除外する(アイコン列など)。
    /// 配列長が列数より短い場合、残りの列は固定幅扱いになる。
    /// </param>
    public static void AttachAutoSize(ListView listView, double?[] ratios)
    {
        listView.SizeChanged += (_, e) =>
        {
            // 高さのみの変化では再計算不要(無駄なレイアウトパスを避ける)。
            if (e.WidthChanged) Apply(listView, ratios);
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
