using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Purge.UI;

/// <summary>
/// 一覧に横ホイール(チルトホイール/トラックパッドの横スワイプ)でのスクロールを追加するヘルパー。
///
/// WPFは水平ホイールのWindowsメッセージ(WM_MOUSEHWHEEL)を標準のイベントとして扱わないため、
/// 既定では横スクロールが一切効かない。ここではウィンドウのメッセージフックで直接
/// WM_MOUSEHWHEELを受け取り、マウスカーソル下のScrollViewerを横スクロールさせる。
/// あわせてShift+ホイールも横スクロールに割り当てる(Windowsの一般的な慣習)。
/// </summary>
public static class HorizontalScrollSupport
{
    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>ホイール1ノッチ(Delta=120)あたりの横移動量。</summary>
    private const double ScrollAmountPerNotch = 48;

    /// <summary>
    /// ウィンドウ全体で横ホイールを有効化する。各ウィンドウのコンストラクタから一度呼ぶ。
    /// カーソル下にScrollViewerがあればそれを横スクロールするため、対象ごとの登録は不要。
    /// </summary>
    public static void AttachToWindow(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(window);
            source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;

                // wParamの上位16bitに符号付きのDeltaが入る。
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (delta == 0) return IntPtr.Zero;

                var scrollViewer = FindScrollViewerUnderMouse(window);
                if (scrollViewer == null) return IntPtr.Zero;

                // 横ホイールは右方向が正。右に倒したら右へスクロールさせる。
                scrollViewer.ScrollToHorizontalOffset(
                    scrollViewer.HorizontalOffset + (delta / 120.0 * ScrollAmountPerNotch));

                handled = true;
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>
    /// ItemsControl(ListView等)にShift+ホイールでの横スクロールを有効化する。
    /// チルトホイールを持たないマウス向けの代替操作。
    /// </summary>
    public static void AttachShiftWheel(ItemsControl itemsControl)
    {
        itemsControl.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift) return;

            var scrollViewer = FindScrollViewer(itemsControl);
            if (scrollViewer == null) return;

            scrollViewer.ScrollToHorizontalOffset(
                scrollViewer.HorizontalOffset - (e.Delta / 120.0 * ScrollAmountPerNotch));

            // 縦スクロールが同時に走らないよう処理済みにする。
            e.Handled = true;
        };
    }

    /// <summary>
    /// マウスカーソルの真下にある要素から親方向に辿り、最初に見つかったScrollViewerを返す。
    /// </summary>
    private static ScrollViewer? FindScrollViewerUnderMouse(Window window)
    {
        var position = Mouse.GetPosition(window);
        var hit = window.InputHitTest(position) as DependencyObject;

        while (hit != null)
        {
            if (hit is ScrollViewer sv) return sv;
            hit = VisualTreeHelper.GetParent(hit);
        }

        return null;
    }

    /// <summary>
    /// ビジュアルツリーを下方向に辿って内部のScrollViewerを探す。
    /// ListViewのテンプレート内部にあるため直接は取得できない。
    /// </summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }

        return null;
    }
}
