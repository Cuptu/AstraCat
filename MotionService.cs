using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AstraCat;

public sealed class PageMotionGroup
{
    public IReadOnlyList<Control> LeftItems { get; set; } = Array.Empty<Control>();
    public IReadOnlyList<Control> RightItems { get; set; } = Array.Empty<Control>();
}

/// <summary>
/// Coordinates application motion with an additive timeline so interrupted
/// interactions continue smoothly from their current visual state.
/// </summary>
public sealed class MotionService
{
    private static readonly Easing Linear = new ClampedLinearEasing();
    private static readonly Easing FluentInWeak = new PolynomialInEasing(EasingStrength.Weak);
    private static readonly Easing FluentOutWeak = new PolynomialOutEasing(EasingStrength.Weak);
    private static readonly Easing FluentOut = new PolynomialOutEasing();
    private static readonly Easing FluentOutStrong = new PolynomialOutEasing(EasingStrength.Strong);
    private static readonly Easing FluentOutExtraStrong = new PolynomialOutEasing(EasingStrength.ExtraStrong);
    private static readonly Easing FluentInOutWeak = new PolynomialInOutEasing(EasingStrength.Weak);
    private static readonly Easing BackOutWeak = new BackOutMotionEasing(EasingStrength.Weak);
    private static readonly Easing BackOut = new BackOutMotionEasing();
    private static readonly Easing DrawerCurve = new DrawerEasing();

    private readonly AdditiveAnimationScheduler _scheduler = new();
    private readonly Dictionary<Control, int> _ids = new();
    private readonly Dictionary<Control, ScaleTransform> _scales = new();
    private readonly Dictionary<Control, TranslateTransform> _translations = new();
    private readonly Dictionary<Control, RotateTransform> _rotations = new();
    private readonly Dictionary<Control, string> _modelListStates = new();
    private readonly HashSet<Control> _pressedButtons = new();
    private readonly HashSet<Control> _pressedNavigation = new();
    private int _nextId;

    public async Task WindowEnterAsync(Window window, Control root, IReadOnlyList<Control> sidebarItems)
    {
        _scheduler.Attach(window);
        var translate = new TranslateTransform(0, 16);
        root.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        root.RenderTransform = translate;
        // Keep the transparent HWND itself at full opacity. Animating the
        // chrome preserves the intended fade without making a fully
        // transparent Windows layered window untargetable during startup.
        window.Opacity = 1;
        root.Opacity = 0;
        PrepareSidebar(sidebarItems);

        var windowMotion = _scheduler.Start(new[]
        {
            Track(delta => root.Opacity = Math.Clamp(root.Opacity + delta, 0, 1), 1, 220, 30, Linear),
            Track(delta => translate.Y += delta, -16, 260, 30, FluentOut)
        }, "Window Enter");

        await Task.WhenAll(windowMotion, StaggerSidebarInAsync(sidebarItems));
        root.Opacity = 1;
        root.RenderTransform = null;
    }

    public async Task WindowExitAsync(Window window, Control root)
    {
        var rotate = new RotateTransform(0);
        var translate = new TranslateTransform(0, 0);
        var scale = new ScaleTransform(1, 1);
        var transforms = new TransformGroup();
        transforms.Children.Add(rotate);
        transforms.Children.Add(translate);
        transforms.Children.Add(scale);
        root.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        root.RenderTransform = transforms;
        root.IsHitTestVisible = false;

        await _scheduler.Start(new[]
        {
            Track(delta => root.Opacity = Math.Clamp(root.Opacity + delta, 0, 1), -root.Opacity, 140, 40, FluentOutWeak),
            Track(delta => { scale.ScaleX = Math.Max(0, scale.ScaleX + delta); scale.ScaleY = Math.Max(0, scale.ScaleY + delta); }, -0.12, 180, 0, Linear),
            Track(delta => translate.Y += delta, 20, 180, 0, FluentOutWeak),
            Track(delta => rotate.Angle += delta, 0.6, 180, 0, FluentInOutWeak)
        }, "Window Exit");

        // Leave a short tail after the visual transition before closing.
        await Task.Delay(50);
    }

    public async Task PageTransitionAsync(
        Control currentPage,
        Control targetPage,
        PageMotionGroup currentGroup,
        PageMotionGroup targetGroup,
        CancellationToken token)
    {
        if (ReferenceEquals(currentPage, targetPage))
        {
            currentPage.IsVisible = true;
            currentPage.Opacity = 1;
            return;
        }

        var exitName = $"Page Exit {Id(currentPage)}";
        var enterName = $"Page Enter {Id(targetPage)}";
        using var registration = token.Register(() =>
        {
            _scheduler.Stop(exitName);
            _scheduler.Stop(enterName);
            _scheduler.Stop($"{exitName} Left");
            _scheduler.Stop($"{exitName} Right");
            _scheduler.Stop($"{enterName} Left");
            _scheduler.Stop($"{enterName} Right");
        });

        var exitLeftTask = StartLeftExit(currentGroup.LeftItems, $"{exitName} Left");
        var exitRightTask = StartRightExit(currentGroup.RightItems, $"{exitName} Right");
        await Task.Delay(80, token);

        currentPage.IsVisible = false;
        PrepareLeftItems(targetGroup.LeftItems);
        PrepareRightItems(targetGroup.RightItems);
        targetPage.IsVisible = true;
        targetPage.Opacity = 1;

        await Task.Delay(20, token);
        var enterLeftTask = StartLeftEnter(targetGroup.LeftItems, $"{enterName} Left");
        var enterRightTask = StartRightEnter(targetGroup.RightItems, $"{enterName} Right");

        await Task.WhenAll(enterLeftTask, enterRightTask);
        await IgnoreCancellation(Task.WhenAll(exitLeftTask, exitRightTask));

        token.ThrowIfCancellationRequested();

        foreach (var item in targetGroup.LeftItems)
        {
            item.Opacity = 1;
            item.IsHitTestVisible = true;
        }
        foreach (var item in targetGroup.RightItems)
        {
            item.Opacity = 1;
            item.IsHitTestVisible = true;
        }
    }

    public async Task ContentTransitionAsync(
        Control current,
        Control target,
        IReadOnlyList<Control> currentItems,
        IReadOnlyList<Control> targetItems,
        CancellationToken token)
    {
        var exitName = $"Content Exit {Id(current)}";
        var enterName = $"Content Enter {Id(target)}";
        using var registration = token.Register(() =>
        {
            _scheduler.Stop(exitName);
            _scheduler.Stop(enterName);
        });

        var exitTask = StartRightExit(currentItems, exitName);
        await Task.Delay(130, token);
        PrepareRightItems(targetItems);
        current.IsVisible = false;
        target.IsVisible = true;
        await Task.Delay(30, token);
        await StartRightEnter(targetItems, enterName);
        await IgnoreCancellation(exitTask);
        ResetItems(currentItems);
        ResetItems(targetItems);
    }

    public async Task SetCatalogLoadingAsync(Control content, Control overlay, bool visible)
    {
        const string animationName = "Model Catalog Loading";
        if (visible)
        {
            overlay.IsVisible = true;
            overlay.IsHitTestVisible = true;
            content.IsHitTestVisible = false;
            try
            {
                await _scheduler.Start(new[]
                {
                    OpacityTrack(content, 0.18 - content.Opacity, 80, 0, Linear),
                    OpacityTrack(overlay, 1 - overlay.Opacity, 130, 30, FluentOutWeak)
                }, animationName);
            }
            catch (TaskCanceledException) { }
            return;
        }

        try
        {
            await _scheduler.Start(new[]
            {
                OpacityTrack(overlay, -overlay.Opacity, 90, 0, Linear),
                OpacityTrack(content, 1 - content.Opacity, 150, 25, FluentOutWeak)
            }, animationName);
        }
        catch (TaskCanceledException) { }
        overlay.IsVisible = false;
        overlay.IsHitTestVisible = false;
        content.Opacity = 1;
        content.IsHitTestVisible = true;
    }

    public async Task SetTranslationDrawerVisibleAsync(
        Control layer,
        Control backdrop,
        Control panel,
        bool visible,
        CancellationToken token)
    {
        var animationName = $"Translation Drawer {Id(panel)}";
        using var registration = token.Register(() => _scheduler.Stop(animationName));
        var translate = panel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        panel.RenderTransform = translate;
        var offscreenX = Math.Max(412, panel.Bounds.Width + 12);

        if (visible)
        {
            layer.IsVisible = true;
            layer.IsHitTestVisible = true;
            backdrop.Opacity = 0;
            translate.X = offscreenX;
            try
            {
                await _scheduler.Start(new[]
                {
                    OpacityTrack(backdrop, 1 - backdrop.Opacity, 150, 0, Linear),
                    Track(delta => translate.X += delta, -translate.X, 180, 0, DrawerCurve)
                }, animationName);
            }
            catch (TaskCanceledException) { }
            backdrop.Opacity = 1;
            translate.X = 0;
            return;
        }

        layer.IsHitTestVisible = false;
        try
        {
            await _scheduler.Start(new[]
            {
                OpacityTrack(backdrop, -backdrop.Opacity, 150, 0, Linear),
                Track(delta => translate.X += delta, offscreenX - translate.X, 180, 0, DrawerCurve)
            }, animationName);
        }
        catch (TaskCanceledException) { }
        backdrop.Opacity = 0;
        translate.X = offscreenX;
        layer.IsVisible = false;
    }

    public async Task SlideContentTransitionAsync(
        Control current,
        Control target,
        bool forward,
        CancellationToken token,
        Action? afterExit = null)
    {
        // Let the old page leave before crossing the stage barrier and bringing
        // the new page home. Deliberately avoid overshoot in this compact view.
        const string transitionName = "Model Detail Page Switch";
        using var registration = token.Register(() => _scheduler.Stop(transitionName));

        var currentTranslate = EnsureHorizontalTranslate(current, 0);
        var targetWasInitialized = target.RenderTransform is TranslateTransform;
        var targetTranslate = EnsureHorizontalTranslate(target, forward ? 40 : -50);
        if (!targetWasInitialized && !target.IsVisible)
            target.Opacity = 0;

        current.IsHitTestVisible = false;
        target.IsHitTestVisible = false;
        target.IsVisible = true;

        var exitX = forward ? -50d : 50d;
        try
        {
            await _scheduler.Start(new[]
            {
                // Stage A: fade and translate the outgoing page linearly.
                OpacityTrack(current, -current.Opacity, 70, 0, Linear),
                Track(delta => currentTranslate.X += delta,
                    exitX - currentTranslate.X, 85, 0, Linear),

                // The first track after the barrier starts only after stage A completes.
                Track(_ =>
                {
                    afterExit?.Invoke();
                    if (forward) current.IsVisible = false;
                }, 1, 0, 0, Linear, after: true),

                // Stage B: seamlessly slide and fade in target right after barrier
                OpacityTrack(target, 1 - target.Opacity, 90, 0, Linear),
                Track(delta => targetTranslate.X += delta,
                    -targetTranslate.X, 170, 0, FluentOutExtraStrong),

                // Restore input and finalize visibility after completion
                Track(_ =>
                {
                    current.IsVisible = false;
                    current.IsHitTestVisible = false;
                    target.IsHitTestVisible = true;
                }, 1, 0, 0, Linear, after: true)
            }, transitionName);
        }
        finally
        {
            if (forward)
            {
                current.IsVisible = false;
                current.IsHitTestVisible = false;
                target.IsVisible = true;
                target.IsHitTestVisible = true;
                target.Opacity = 1;
                targetTranslate.X = 0;
            }
            else
            {
                target.IsVisible = true;
                target.IsHitTestVisible = true;
                target.Opacity = 1;
                targetTranslate.X = 0;
                current.IsVisible = false;
                current.IsHitTestVisible = false;
            }
        }
    }

    public Task ShowDownloadTaskButtonAsync(Control button)
    {
        var scale = button.RenderTransform as ScaleTransform ?? new ScaleTransform(0, 0);
        button.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        button.RenderTransform = scale;
        button.Opacity = 1;
        button.IsHitTestVisible = true;
        return _scheduler.Start(new[]
        {
            ScaleTrack(scale, 0.3 - scale.ScaleX, 500, 60, FluentOutWeak),
            ScaleTrack(scale, 0.7, 360, 60, FluentOut)
        }, $"Download Button Show {Id(button)}");
    }

    public async Task HideDownloadTaskButtonAsync(Control button)
    {
        var scale = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.IsHitTestVisible = false;
        try
        {
            await _scheduler.Start(new[]
            {
                ScaleTrack(scale, -scale.ScaleX, 100, 0, FluentInWeak)
            }, $"Download Button Show {Id(button)}");
        }
        catch (TaskCanceledException)
        {
            return;
        }
        scale.ScaleX = 0;
        scale.ScaleY = 0;
    }

    public async void RibbleDownloadTask(Border ripple)
    {
        var scale = ripple.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        ripple.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        ripple.RenderTransform = scale;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        ripple.Opacity = 0.5;
        try
        {
            await _scheduler.Start(new[]
            {
                ScaleTrack(scale, 13, 1000, 0, new PolynomialInOutEasing(EasingStrength.Strong, 0.3)),
                OpacityTrack(ripple, -0.5, 1000, 0, Linear)
            }, $"Download Button Ripple {Id(ripple)}");
        }
        catch (TaskCanceledException)
        {
            return;
        }
        ripple.Opacity = 0;
    }

    public void AnimateDownloadTaskProgress(Border fill, double targetHeight)
    {
        targetHeight = Math.Clamp(targetHeight, 0, 40);
        _ = _scheduler.Start(new[]
        {
            Track(delta => fill.Height = Math.Clamp(fill.Height + delta, 0, 40),
                targetHeight - fill.Height, 180, 0, FluentOutWeak)
        }, $"Download Button Progress {Id(fill)}");
    }

    public async Task SetDownloadTaskPanelVisibleAsync(Border panel, bool visible)
    {
        var translate = panel.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 8);
        panel.RenderTransform = translate;
        var name = $"Download Task Panel {Id(panel)}";
        if (visible)
        {
            panel.IsVisible = true;
            panel.IsHitTestVisible = true;
            try
            {
                await _scheduler.Start(new[]
                {
                    OpacityTrack(panel, 1 - panel.Opacity, 140, 0, Linear),
                    TranslateYOrXTrack(translate, -translate.Y, 180, 0, FluentOutWeak, horizontal: false)
                }, name);
            }
            catch (TaskCanceledException) { }
            return;
        }

        panel.IsHitTestVisible = false;
        try
        {
            await _scheduler.Start(new[]
            {
                OpacityTrack(panel, -panel.Opacity, 110, 0, FluentInWeak),
                TranslateYOrXTrack(translate, 8 - translate.Y, 120, 0, FluentInWeak, horizontal: false)
            }, name);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        panel.IsVisible = false;
    }

    private static TranslateTransform EnsureHorizontalTranslate(Control control, double initialX)
    {
        if (control.RenderTransform is TranslateTransform translate) return translate;
        translate = new TranslateTransform(initialX, 0);
        control.RenderTransform = translate;
        return translate;
    }

    public void AnimateModelSelectionIndicator(Control indicator, double targetY)
    {
        var translate = indicator.RenderTransform as TranslateTransform ?? new TranslateTransform();
        indicator.RenderTransform = translate;
        _ = _scheduler.Start(new[]
        {
            Track(delta => translate.Y += delta,
                targetY - translate.Y, 250, 0, FluentOutExtraStrong)
        }, "Model Selection Indicator");
    }

    public Task PageEnterAsync(IReadOnlyList<Control> items, CancellationToken token)
    {
        PrepareRightItems(items);
        return StartRightEnter(items, $"Standalone Page Enter {RuntimeHelpers.GetHashCode(items)}");
    }

    public async Task AnimateCardSwapAsync(Border panel, Control chevron, bool expand, double expandedHeight)
    {
        // Expose content before expansion, keep it visible while collapsing,
        // and remove it only after the final clipped frame.
        panel.IsVisible = true;
        panel.Opacity = 1;
        panel.IsHitTestVisible = expand;
        var rotate = RotationFor(chevron);
        var id = Id(panel);
        try
        {
            var heightDelta = (expand ? expandedHeight : 0) - panel.MaxHeight;
            // Drive card height and arrow as independent named groups. This
            // keeps the 250 ms arrow moving in parallel with every height
            // branch, including long two-stage cards.
            var heightTask = _scheduler.Start(CardHeightTracks(panel, heightDelta), $"Card Height {id}");
            var arrowTask = _scheduler.Start(new[]
            {
                Track(delta => rotate.Angle += delta,
                    (expand ? 180 : 0) - rotate.Angle, 250, 0, FluentOutExtraStrong)
            }, $"Card Chevron {id}");
            await Task.WhenAll(heightTask, arrowTask);
            panel.MaxHeight = expand ? expandedHeight : 0;
            panel.IsHitTestVisible = expand;
            if (!expand) panel.IsVisible = false;
        }
        catch (TaskCanceledException)
        {
            // A rapid second click continues from the current incremental value.
        }
    }

    private static IEnumerable<AnimationTrack> CardHeightTracks(Border panel, double delta)
    {
        var distance = Math.Abs(delta);
        if (distance <= 800)
        {
            yield return Track(value => panel.MaxHeight = Math.Max(0, panel.MaxHeight + value),
                delta, 150, 0, FluentOutExtraStrong);
            yield break;
        }

        double easeLength;
        double easeTime;
        double initialSpeed;
        if (delta < 0)
        {
            easeLength = 200;
            easeTime = 150;
            initialSpeed = (distance - easeLength) / 0.1;
        }
        else if (distance > 3000)
        {
            initialSpeed = 5000;
            easeLength = distance - initialSpeed * 0.3;
            easeTime = 400;
        }
        else
        {
            easeLength = 150;
            easeTime = 200;
            initialSpeed = 4000;
        }

        var direction = Math.Sign(delta);
        var linearDistance = (distance - easeLength) * direction;
        var linearDuration = Math.Max(1, (int)Math.Round(Math.Abs(linearDistance) / initialSpeed * 1000));
        yield return Track(value => panel.MaxHeight = Math.Max(0, panel.MaxHeight + value),
            linearDistance, linearDuration, 0, Linear);
        yield return new AnimationTrack
        {
            ApplyDelta = value => panel.MaxHeight = Math.Max(0, panel.MaxHeight + value),
            Value = easeLength * direction,
            Duration = easeTime,
            Easing = new InitialVelocityOutEasing(initialSpeed, easeTime / 1000, easeLength),
            After = true
        };
    }

    public void AnimateSidebarIndicator(Control indicator, double targetOffset)
    {
        var translate = TranslationFor(indicator);
        var delta = targetOffset - translate.Y;
        _ = Observe(_scheduler.Start(new[]
        {
            TranslateYOrXTrack(translate, delta, 280, 0, FluentOutStrong, horizontal: false)
        }, $"Navigation Indicator {Id(indicator)}"));
    }

    public async Task AnimateWorkspaceShellAsync(
        Control sidebar,
        Control splitter,
        Control divider,
        ColumnDefinition sidebarColumn,
        ColumnDefinition dividerColumn,
        Control projectHeader,
        Control projectLayout,
        Control sectionHost,
        Control workspaceSurface,
        bool fullscreen,
        CancellationToken token)
    {
        const string animationName = "Astra Workspace Shell";
        using var registration = token.Register(() => _scheduler.Stop(animationName));

        var startSidebarWidth = sidebarColumn.Width.Value;
        var startDividerWidth = dividerColumn.Width.Value;
        var targetSidebarWidth = fullscreen ? 0d : 232d;
        var targetDividerWidth = fullscreen ? 0d : 7d;
        var startSidebarOpacity = sidebar.IsVisible ? sidebar.Opacity : 0d;
        var startHeaderOpacity = projectHeader.IsVisible ? projectHeader.Opacity : 0d;
        var targetOpacity = fullscreen ? 0d : 1d;
        var startLayoutMargin = projectLayout.Margin;
        var targetLayoutMargin = fullscreen ? new Thickness(0) : new Thickness(22, 22, 22, 24);
        var startHostMargin = sectionHost.Margin;
        var targetHostMargin = fullscreen ? new Thickness(0) : new Thickness(0, 25, 0, 0);
        var startScale = fullscreen ? 0.972 : 1d;
        var targetScale = fullscreen ? 1d : 0.972;
        var workspaceScale = new ScaleTransform(startScale, startScale);
        workspaceSurface.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        workspaceSurface.RenderTransform = workspaceScale;

        sidebar.IsVisible = true;
        splitter.IsVisible = true;
        divider.IsVisible = true;
        projectHeader.IsVisible = true;
        sidebar.IsHitTestVisible = false;
        splitter.IsHitTestVisible = false;
        projectHeader.IsHitTestVisible = false;

        var progress = 0d;
        try
        {
            await _scheduler.Start(new[]
            {
                Track(delta =>
                {
                    progress = Math.Clamp(progress + delta, 0, 1);
                    static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
                    sidebarColumn.Width = new GridLength(Lerp(startSidebarWidth, targetSidebarWidth, progress));
                    dividerColumn.Width = new GridLength(Lerp(startDividerWidth, targetDividerWidth, progress));
                    sidebar.Opacity = Lerp(startSidebarOpacity, targetOpacity, progress);
                    splitter.Opacity = sidebar.Opacity;
                    divider.Opacity = sidebar.Opacity;
                    projectHeader.Opacity = Lerp(startHeaderOpacity, targetOpacity, progress);
                    projectLayout.Margin = new Thickness(
                        Lerp(startLayoutMargin.Left, targetLayoutMargin.Left, progress),
                        Lerp(startLayoutMargin.Top, targetLayoutMargin.Top, progress),
                        Lerp(startLayoutMargin.Right, targetLayoutMargin.Right, progress),
                        Lerp(startLayoutMargin.Bottom, targetLayoutMargin.Bottom, progress));
                    sectionHost.Margin = new Thickness(
                        Lerp(startHostMargin.Left, targetHostMargin.Left, progress),
                        Lerp(startHostMargin.Top, targetHostMargin.Top, progress),
                        Lerp(startHostMargin.Right, targetHostMargin.Right, progress),
                        Lerp(startHostMargin.Bottom, targetHostMargin.Bottom, progress));
                }, 1, 210, 0, FluentOutWeak),
                ScaleTrack(workspaceScale, targetScale - startScale, 240, 0, FluentOutStrong)
            }, animationName);
        }
        finally
        {
            sidebarColumn.Width = new GridLength(targetSidebarWidth);
            dividerColumn.Width = new GridLength(targetDividerWidth);
            sidebar.Opacity = 1;
            splitter.Opacity = 1;
            divider.Opacity = 1;
            projectHeader.Opacity = 1;
            projectLayout.Margin = targetLayoutMargin;
            sectionHost.Margin = targetHostMargin;
            sidebar.IsVisible = !fullscreen;
            splitter.IsVisible = !fullscreen;
            divider.IsVisible = !fullscreen;
            projectHeader.IsVisible = !fullscreen;
            sidebar.IsHitTestVisible = !fullscreen;
            splitter.IsHitTestVisible = !fullscreen;
            projectHeader.IsHitTestVisible = !fullscreen;
            workspaceSurface.RenderTransform = null;
        }
    }

    public void AnimateModelListItem(Border row, bool hovered, bool pressed)
    {
        var state = pressed ? "MouseDown" : hovered ? "MouseOver" : "Idle";
        if (_modelListStates.TryGetValue(row, out var previous) && previous == state) return;
        _modelListStates[row] = state;

        var layer = row.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("modelRowHover"));
        if (layer is null) return;

        var layerScale = ScaleFor(layer, 0.8);
        var rowScale = ScaleFor(row);
        var background = layer.Background as SolidColorBrush;
        if (background is null)
        {
            background = new SolidColorBrush(Color.Parse("#E0EAFD"));
            layer.Background = background;
        }

        var name = $"Model Row State {Id(row)}";
        if (hovered)
        {
            var targetColor = pressed
                ? Color.Parse("#D5E6FD")
                : Color.FromArgb(190, 224, 234, 253);
            _ = Observe(_scheduler.Start(new[]
            {
                ColorTrack(background, targetColor, 120, Linear),
                Track(delta => layer.Opacity = Math.Clamp(layer.Opacity + delta, 0, 1),
                    1 - layer.Opacity, 120, 0, FluentOut),
                Track(delta => { layerScale.ScaleX += delta; layerScale.ScaleY += delta; },
                    1 - layerScale.ScaleX, 192, 0, FluentOut),
                Track(delta => { rowScale.ScaleX += delta; rowScale.ScaleY += delta; },
                    (pressed ? 0.98 : 1) - rowScale.ScaleX, pressed ? 108 : 144, 0, FluentOut)
            }, name));
            return;
        }

        _ = Observe(_scheduler.Start(new[]
        {
            Track(delta => layer.Opacity = Math.Clamp(layer.Opacity + delta, 0, 1),
                -layer.Opacity, 180, 0, Linear),
            ColorTrack(background, Color.Parse("#E0EAFD"), 180, Linear),
            Track(delta => { rowScale.ScaleX += delta; rowScale.ScaleY += delta; },
                1 - rowScale.ScaleX, 540, 0, FluentOut),
            Track(delta => { layerScale.ScaleX += delta; layerScale.ScaleY += delta; },
                0.996 - layerScale.ScaleX, 180, 0, FluentOut),
            Track(delta => { layerScale.ScaleX += delta; layerScale.ScaleY += delta; },
                -0.246, 1, 0, Linear, after: true)
        }, name));
    }

    // Hover changes only color; scale feedback begins on pointer down.
    public void AnimateButtonEnter(Control control, bool iconButton) { }

    public void AnimateButtonPress(Control control, bool iconButton)
    {
        _pressedButtons.Add(control);
        var surface = ButtonSurface(control);
        var scale = ScaleFor(surface);
        var name = $"Button Scale {Id(control)}";
        if (iconButton)
        {
            _ = Observe(_scheduler.Start(new[]
            {
                ScaleTrack(scale, 0.8 - scale.ScaleX, 400, 0, FluentOutStrong)
            }, name));
            return;
        }

        _ = Observe(_scheduler.Start(new[]
        {
            ScaleTrack(scale, 0.955 - scale.ScaleX, 80, 0, FluentOutExtraStrong),
            ScaleTrack(scale, -0.01, 700, 0, FluentOut)
        }, name));
    }

    public void AnimateButtonRelease(Control control, bool iconButton, bool pointerOver = false)
    {
        _pressedButtons.Remove(control);
        var surface = ButtonSurface(control);
        var scale = ScaleFor(surface);
        var name = $"Button Scale {Id(control)}";
        if (iconButton)
        {
            _ = Observe(_scheduler.Start(new[]
            {
                ScaleTrack(scale, 1.05 - scale.ScaleX, 180, 0, FluentOutStrong),
                ScaleTrack(scale, -0.05, 250, 0, FluentOutStrong)
            }, name));
            return;
        }

        _ = Observe(_scheduler.Start(new[]
        {
            ScaleTrack(scale, 1 - scale.ScaleX, 250, 0, FluentOut)
        }, name));
    }

    public void AnimateButtonExit(Control control, bool iconButton)
    {
        _pressedButtons.Remove(control);
        var surface = ButtonSurface(control);
        var scale = ScaleFor(surface);
        _ = Observe(_scheduler.Start(new[]
        {
            ScaleTrack(scale, 1 - scale.ScaleX, iconButton ? 200 : 250, 0, FluentOut)
        }, $"Button Scale {Id(control)}"));
    }

    public void AnimateNavigationEnter(Control button) =>
        StartNavigationState(button, NavigationState.MouseOver);

    public void AnimateNavigationPress(Control button)
    {
        _pressedNavigation.Add(button);
        StartNavigationState(button, NavigationState.MouseDown);
    }

    public void AnimateNavigationRelease(Control button, bool pointerOver = false)
    {
        _pressedNavigation.Remove(button);
        StartNavigationState(button, pointerOver ? NavigationState.MouseOver : NavigationState.Idle);
    }

    public void AnimateNavigationExit(Control button)
    {
        _pressedNavigation.Remove(button);
        StartNavigationState(button, NavigationState.Idle);
    }

    private void StartNavigationState(Control button, NavigationState state)
    {
        var root = button.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Classes.Contains("nav-root")) ?? button;
        var background = button.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(control => control.Name == "NavHoverBack");
        if (background is null) return;

        var rootScale = ScaleFor(root);
        var backgroundScale = ScaleFor(background, 0.75);
        var tracks = new List<AnimationTrack>();
        switch (state)
        {
            case NavigationState.MouseDown:
                tracks.Add(OpacityTrack(background, 1 - background.Opacity, 120, 0, FluentOut));
                tracks.Add(ScaleTrack(backgroundScale, 1 - backgroundScale.ScaleX, 192, 0, FluentOut));
                tracks.Add(ScaleTrack(rootScale, 0.98 - rootScale.ScaleX, 108, 0, FluentOut));
                break;
            case NavigationState.MouseOver:
                tracks.Add(OpacityTrack(background, 1 - background.Opacity, 120, 0, FluentOut));
                tracks.Add(ScaleTrack(backgroundScale, 1 - backgroundScale.ScaleX, 192, 0, FluentOut));
                tracks.Add(ScaleTrack(rootScale, 1 - rootScale.ScaleX, 144, 0, FluentOut));
                break;
            default:
                tracks.Add(OpacityTrack(background, -background.Opacity, 180, 0, Linear));
                tracks.Add(ScaleTrack(rootScale, 1 - rootScale.ScaleX, 540, 0, FluentOut));
                tracks.Add(ScaleTrack(backgroundScale, 0.996 - backgroundScale.ScaleX, 180, 0, FluentOut));
                tracks.Add(ScaleTrack(backgroundScale, -0.246, 1, 0, Linear, after: true));
                break;
        }

        _ = Observe(_scheduler.Start(tracks, $"Navigation State {Id(button)}"));
    }

    private async Task StaggerSidebarInAsync(IReadOnlyList<Control> items)
    {
        var delay = 0;
        var tracks = new List<AnimationTrack>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var translate = TranslationFor(item);
            var targetOpacity = item is TextBlock ? 0.6 : 1;
            tracks.Add(OpacityTrack(item, targetOpacity - item.Opacity, 100, delay, FluentOutWeak));
            tracks.Add(TranslateYOrXTrack(translate, 5, 200, delay, FluentOut, horizontal: true));
            tracks.Add(TranslateYOrXTrack(translate, 20, 240, delay, FluentOut, horizontal: true));
            delay += Math.Max(15 - index, 7) * 2;
        }

        await _scheduler.Start(tracks, "Sidebar Enter");
        foreach (var item in items)
        {
            item.IsHitTestVisible = true;
            item.RenderTransform = null;
        }
    }

    private Task StartLeftEnter(IReadOnlyList<Control> items, string name)
    {
        if (items.Count == 0) return Task.CompletedTask;
        var tracks = new List<AnimationTrack>();
        var delay = 0;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var translate = TranslationFor(item);
            var targetOpacity = item is TextBlock ? 0.6 : 1.0;
            tracks.Add(OpacityTrack(item, targetOpacity, 100, delay, FluentOutWeak));
            tracks.Add(TranslateYOrXTrack(translate, 5, 200, delay, FluentOut, horizontal: true));
            tracks.Add(TranslateYOrXTrack(translate, 20, 300, delay, BackOutWeak, horizontal: true));
            delay += Math.Max(15 - index, 7) * 2;
        }
        return _scheduler.Start(tracks, name);
    }

    private Task StartRightEnter(IReadOnlyList<Control> items, string name)
    {
        if (items.Count == 0) return Task.CompletedTask;
        var tracks = new List<AnimationTrack>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var delay = Math.Min(index * 30, 150);
            var translate = TranslationFor(item);
            tracks.Add(OpacityTrack(item, 1, 160, delay, FluentOutWeak));
            tracks.Add(TranslateYOrXTrack(translate, 5, 320, delay, FluentOut, horizontal: false));
            tracks.Add(TranslateYOrXTrack(translate, 11, 420, delay, BackOut, horizontal: false));
        }
        return _scheduler.Start(tracks, name);
    }

    private Task StartLeftExit(IReadOnlyList<Control> items, string name)
    {
        if (items.Count == 0) return Task.CompletedTask;
        var tracks = new List<AnimationTrack>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            item.IsHitTestVisible = false;
            var translate = TranslationFor(item);
            var delay = items.Count > 0 ? (70 / items.Count) * index : 0;
            tracks.Add(OpacityTrack(item, -item.Opacity, 60, delay, Linear));
            tracks.Add(TranslateYOrXTrack(translate, -6, 60, delay, Linear, horizontal: true));
        }
        return _scheduler.Start(tracks, name);
    }

    private Task StartRightExit(IReadOnlyList<Control> items, string name)
    {
        if (items.Count == 0) return Task.CompletedTask;
        var tracks = new List<AnimationTrack>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            item.IsHitTestVisible = false;
            var translate = TranslationFor(item);
            tracks.Add(OpacityTrack(item, -1, 80, Math.Min(index * 12, 36), Linear));
            tracks.Add(TranslateYOrXTrack(translate, -6, 80, Math.Min(index * 12, 36), Linear, horizontal: false));
        }
        return _scheduler.Start(tracks, name);
    }

    private void PrepareSidebar(IEnumerable<Control> items)
    {
        foreach (var item in items)
        {
            item.Opacity = 0;
            item.IsHitTestVisible = false;
            var translate = TranslationFor(item);
            translate.X = -25;
            translate.Y = 0;
        }
    }

    private void PrepareLeftItems(IEnumerable<Control> items)
    {
        foreach (var item in items)
        {
            item.Opacity = 0;
            item.IsHitTestVisible = false;
            var translate = TranslationFor(item);
            translate.X = -25;
            translate.Y = 0;
        }
    }

    private void PrepareRightItems(IEnumerable<Control> items)
    {
        foreach (var item in items)
        {
            item.Opacity = 0;
            item.IsHitTestVisible = false;
            var translate = TranslationFor(item);
            translate.X = 0;
            translate.Y = -16;
        }
    }

    public void RestorePage(Control page, PageMotionGroup group)
    {
        page.Opacity = 1;
        page.IsVisible = true;
        page.IsHitTestVisible = true;
        RestorePageItems(group);

        var content = page is ScrollViewer { Content: Control scrollContent } ? scrollContent : page;
        if (content is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                child.Opacity = 1;
                child.IsHitTestVisible = true;
                if (_translations.TryGetValue(child, out var t))
                {
                    t.X = 0;
                    t.Y = 0;
                }
            }
        }
    }

    public void RestorePageItems(PageMotionGroup group)
    {
        foreach (var item in group.LeftItems.Concat(group.RightItems))
        {
            item.Opacity = 1;
            item.IsHitTestVisible = true;
            if (_translations.TryGetValue(item, out var translate))
            {
                translate.X = 0;
                translate.Y = 0;
            }
            if (_scales.TryGetValue(item, out var scale))
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }
    }

    private static void ResetItems(IEnumerable<Control> items)
    {
        foreach (var item in items)
        {
            item.Opacity = 1;
            item.IsHitTestVisible = true;
            item.RenderTransform = null;
        }
    }

    private AnimationTrack OpacityTrack(Control target, double value, int duration, int delay, Easing easing, bool after = false) =>
        Track(delta => target.Opacity = Math.Clamp(target.Opacity + delta, 0, 1), value, duration, delay, easing, after);

    private static AnimationTrack ScaleTrack(ScaleTransform target, double value, int duration, int delay, Easing easing, bool after = false) =>
        Track(delta =>
        {
            target.ScaleX = Math.Max(0, target.ScaleX + delta);
            target.ScaleY = Math.Max(0, target.ScaleY + delta);
        }, value, duration, delay, easing, after);

    private static AnimationTrack TranslateYOrXTrack(
        TranslateTransform target, double value, int duration, int delay, Easing easing, bool horizontal, bool after = false) =>
        Track(delta =>
        {
            if (horizontal) target.X += delta;
            else target.Y += delta;
        }, value, duration, delay, easing, after);

    private static AnimationTrack Track(
        Action<double> apply, double value, int duration, int delay, Easing easing, bool after = false) => new()
    {
        ApplyDelta = apply,
        Value = value,
        Duration = duration,
        Delay = delay,
        Easing = easing,
        After = after
    };

    private static AnimationTrack ColorTrack(
        SolidColorBrush brush, Color target, int duration, Easing easing)
    {
        var source = brush.Color;
        var progress = 0d;
        return Track(delta =>
        {
            progress = Math.Clamp(progress + delta, 0, 1);
            static byte Channel(byte from, byte to, double amount) =>
                (byte)Math.Clamp((int)Math.Round(from + (to - from) * amount), 0, 255);
            brush.Color = Color.FromArgb(
                Channel(source.A, target.A, progress),
                Channel(source.R, target.R, progress),
                Channel(source.G, target.G, progress),
                Channel(source.B, target.B, progress));
        }, 1, duration, 0, easing);
    }

    private Control ButtonSurface(Control button) =>
        button.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Name == "ButtonSurface") ?? button;

    private ScaleTransform ScaleFor(Control control, double initial = 1)
    {
        if (_scales.TryGetValue(control, out var scale)) return scale;
        scale = new ScaleTransform(initial, initial);
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        control.RenderTransform = scale;
        _scales[control] = scale;
        return scale;
    }

    private TranslateTransform TranslationFor(Control control)
    {
        if (_translations.TryGetValue(control, out var translate))
        {
            control.RenderTransform = translate;
            return translate;
        }
        translate = new TranslateTransform();
        control.RenderTransform = translate;
        _translations[control] = translate;
        return translate;
    }

    private RotateTransform RotationFor(Control control)
    {
        if (_rotations.TryGetValue(control, out var rotate)) return rotate;
        rotate = new RotateTransform();
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        control.RenderTransform = rotate;
        _rotations[control] = rotate;
        return rotate;
    }

    private int Id(Control control)
    {
        if (_ids.TryGetValue(control, out var id)) return id;
        id = ++_nextId;
        _ids[control] = id;
        return id;
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (TaskCanceledException) { }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (TaskCanceledException) { }
    }

    private enum NavigationState
    {
        Idle,
        MouseOver,
        MouseDown
    }
}
