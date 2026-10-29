using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WhisperBatch.App.Infrastructure;

/// <summary>
/// 画面の動き（アニメーション）の方針を 1 か所にまとめる。
///
/// - 操作の分かりやすさのための短い動きだけを使う（0.12〜0.22 秒、点滅や大きな拡大縮小はしない）
/// - Windows の「アニメーション効果」がオフのときは、位置の移動や弾む動きをやめ、
///   必要なものだけ透明度の変化にするか、即座に切り替える
/// </summary>
public static class Motion
{
    /// <summary>開発時の確認用: 環境変数 WHISPER_BATCH_REDUCE_MOTION=1 で、システム設定を変えずに「動きを減らす」表示にする</summary>
    private static readonly bool ForcedReduce = Environment.GetEnvironmentVariable("WHISPER_BATCH_REDUCE_MOTION") == "1";

    /// <summary>動きを減らすか（Windows の 設定 > アクセシビリティ > 視覚効果 > アニメーション効果）</summary>
    public static bool Reduce => ForcedReduce || !SystemParameters.ClientAreaAnimation;

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static DoubleAnimation Anim(double from, double to, double seconds) =>
        new(from, to, TimeSpan.FromSeconds(seconds)) { EasingFunction = EaseOut };

    // 1. キュー行の追加: 短くフェードし、少し上からスライドして入る（動きを減らす場合はフェードのみ）

    public static readonly DependencyProperty EnterOnLoadProperty = DependencyProperty.RegisterAttached(
        "EnterOnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is FrameworkElement fe && (bool)e.NewValue) fe.Loaded += (_, _) => Enter(fe, slide: true);
        }));

    public static void SetEnterOnLoad(DependencyObject d, bool value) => d.SetValue(EnterOnLoadProperty, value);
    public static bool GetEnterOnLoad(DependencyObject d) => (bool)d.GetValue(EnterOnLoadProperty);

    public static void Enter(UIElement element, bool slide, double fromY = -6)
    {
        element.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, Reduce ? 0.15 : 0.22));
        if (slide && !Reduce)
        {
            var t = new TranslateTransform();
            element.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.YProperty, Anim(fromY, 0, 0.22));
        }
    }

    // 2. 状態アイコン: 完了・失敗時に 1 回だけ小さく弾む（動きを減らす場合は弾まない）

    public static readonly DependencyProperty BounceKeyProperty = DependencyProperty.RegisterAttached(
        "BounceKey", typeof(int), typeof(Motion), new PropertyMetadata(0, (d, e) =>
        {
            if (d is FrameworkElement fe && (int)e.NewValue > (int)e.OldValue && (int)e.OldValue >= 0 && fe.IsLoaded) Bounce(fe);
        }));

    public static void SetBounceKey(DependencyObject d, int value) => d.SetValue(BounceKeyProperty, value);
    public static int GetBounceKey(DependencyObject d) => (int)d.GetValue(BounceKeyProperty);

    public static void Bounce(FrameworkElement element)
    {
        if (Reduce) return;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.32) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.12)), EaseOut));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.32)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // 3〜6. 内容が変わったときのクロスフェード（ラベル、結果表示、下部のバナー）

    public static readonly DependencyProperty FadeKeyProperty = DependencyProperty.RegisterAttached(
        "FadeKey", typeof(object), typeof(Motion), new PropertyMetadata(null, (d, e) =>
        {
            if (d is not UIElement el || !(el as FrameworkElement)?.IsLoaded == true || Equals(e.OldValue, e.NewValue)) return;
            var slide = GetFadeSlides(d);
            if (Reduce && !GetFadeWhenReduced(d)) return;
            Enter(el, slide, fromY: 6);
        }));

    public static void SetFadeKey(DependencyObject d, object? value) => d.SetValue(FadeKeyProperty, value);
    public static object? GetFadeKey(DependencyObject d) => d.GetValue(FadeKeyProperty);

    /// <summary>フェードと一緒に下から少しスライドするか（下部のバナー）</summary>
    public static readonly DependencyProperty FadeSlidesProperty = DependencyProperty.RegisterAttached(
        "FadeSlides", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    public static void SetFadeSlides(DependencyObject d, bool value) => d.SetValue(FadeSlidesProperty, value);
    public static bool GetFadeSlides(DependencyObject d) => (bool)d.GetValue(FadeSlidesProperty);

    /// <summary>動きを減らす設定でも透明度の変化は残すか（結果表示とバナー）</summary>
    public static readonly DependencyProperty FadeWhenReducedProperty = DependencyProperty.RegisterAttached(
        "FadeWhenReduced", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    public static void SetFadeWhenReduced(DependencyObject d, bool value) => d.SetValue(FadeWhenReducedProperty, value);
    public static bool GetFadeWhenReduced(DependencyObject d) => (bool)d.GetValue(FadeWhenReducedProperty);

    // 5. ドラッグ中の強調表示: フェードで現れる・消える（動きを減らす場合は即座に表示）

    public static readonly DependencyProperty FadeVisibleProperty = DependencyProperty.RegisterAttached(
        "FadeVisible", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not UIElement el) return;
            var visible = (bool)e.NewValue;
            if (Reduce)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = visible ? 1 : 0;
                el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            if (visible) el.Visibility = Visibility.Visible;
            var a = Anim(el.Opacity, visible ? 1 : 0, 0.12);
            if (!visible) a.Completed += (_, _) => { if (!GetFadeVisible(el)) el.Visibility = Visibility.Collapsed; };
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }));

    public static void SetFadeVisible(DependencyObject d, bool value) => d.SetValue(FadeVisibleProperty, value);
    public static bool GetFadeVisible(DependencyObject d) => (bool)d.GetValue(FadeVisibleProperty);
}
