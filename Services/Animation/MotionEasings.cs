using System;
using Avalonia.Animation.Easings;

namespace AstraCat;

// Centralized motion curves keep interaction and page transitions consistent.
public enum EasingStrength
{
    Weak = 2,
    Middle = 3,
    Strong = 4,
    ExtraStrong = 5
}

public sealed class ClampedLinearEasing : Easing
{
    public override double Ease(double progress) => Math.Clamp(progress, 0, 1);
}

// Responsive drawer curve: cubic-bezier(0.16, 1, 0.3, 1).
// CSS cubic-bezier easing maps time through
// the x component, so solve x(t)=progress before returning y(t).
public sealed class DrawerEasing : Easing
{
    private const double X1 = 0.16;
    private const double Y1 = 1.0;
    private const double X2 = 0.3;
    private const double Y2 = 1.0;

    public override double Ease(double progress)
    {
        var x = Math.Clamp(progress, 0, 1);
        var t = x;
        for (var index = 0; index < 8; index++)
        {
            var error = Bezier(t, X1, X2) - x;
            var derivative = BezierDerivative(t, X1, X2);
            if (Math.Abs(derivative) < 1e-7) break;
            t = Math.Clamp(t - error / derivative, 0, 1);
        }
        return Bezier(t, Y1, Y2);
    }

    private static double Bezier(double t, double first, double second)
    {
        var inverse = 1 - t;
        return 3 * inverse * inverse * t * first + 3 * inverse * t * t * second + t * t * t;
    }

    private static double BezierDerivative(double t, double first, double second)
    {
        var inverse = 1 - t;
        return 3 * inverse * inverse * first + 6 * inverse * t * (second - first) + 3 * t * t * (1 - second);
    }
}

public sealed class PolynomialInEasing(EasingStrength power = EasingStrength.Middle) : Easing
{
    public override double Ease(double progress) =>
        Math.Pow(Math.Clamp(progress, 0, 1), (int)power);
}

public sealed class PolynomialOutEasing(EasingStrength power = EasingStrength.Middle) : Easing
{
    public override double Ease(double progress) =>
        1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), (int)power);
}

public sealed class PolynomialInOutEasing(
    EasingStrength power = EasingStrength.Middle,
    double middle = 0.5) : Easing
{
    private readonly PolynomialInEasing _in = new(power);
    private readonly PolynomialOutEasing _out = new(power);

    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        return value < middle
            ? middle * _in.Ease(value / middle)
            : middle + (1 - middle) * _out.Ease((value - middle) / (1 - middle));
    }
}

public sealed class InitialVelocityOutEasing : Easing
{
    private readonly double _alpha;

    public InitialVelocityOutEasing(
        double initialPixelsPerSecond,
        double totalSeconds,
        double totalDistance)
    {
        _alpha = Math.Max(0, initialPixelsPerSecond * totalSeconds / totalDistance - 1);
    }

    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        return _alpha == 0 ? value : (_alpha + 1) * value / (1 + _alpha * value);
    }
}

public sealed class BackInMotionEasing(EasingStrength power = EasingStrength.Middle) : Easing
{
    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        var exponent = 3 - (int)power * 0.5;
        return Math.Pow(value, exponent) * Math.Cos(1.5 * Math.PI * (1 - value));
    }
}

public sealed class BackOutMotionEasing(EasingStrength power = EasingStrength.Middle) : Easing
{
    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        var exponent = 3 - (int)power * 0.5;
        return 1 - Math.Pow(1 - value, exponent) * Math.Cos(1.5 * Math.PI * value);
    }
}

public sealed class BackThenOutEasing(
    double middle = 0.7,
    EasingStrength power = EasingStrength.Middle) : Easing
{
    private readonly BackInMotionEasing _in = new(power);
    private readonly PolynomialOutEasing _out = new(power);

    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        return value < middle
            ? middle * _in.Ease(value / middle)
            : middle + (1 - middle) * _out.Ease((value - middle) / (1 - middle));
    }
}

public sealed class InThenBackOutEasing(
    double middle = 0.3,
    EasingStrength power = EasingStrength.Middle) : Easing
{
    private readonly PolynomialInEasing _in = new(power);
    private readonly BackOutMotionEasing _out = new(power);

    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        return value < middle
            ? middle * _in.Ease(value / middle)
            : middle + (1 - middle) * _out.Ease((value - middle) / (1 - middle));
    }
}

public sealed class ElasticInMotionEasing(EasingStrength power = EasingStrength.Middle) : Easing
{
    public override double Ease(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        var p = (int)power + 4;
        return Math.Pow(value, (p - 1) * 0.25) *
               Math.Cos((p - 3.5) * Math.PI * Math.Pow(1 - value, 1.5));
    }
}

public sealed class ElasticOutMotionEasing(EasingStrength power = EasingStrength.Middle) : Easing
{
    public override double Ease(double progress)
    {
        var value = 1 - Math.Clamp(progress, 0, 1);
        var p = (int)power + 4;
        return 1 - Math.Pow(value, (p - 1) * 0.25) *
            Math.Cos((p - 3.5) * Math.PI * Math.Pow(1 - value, 1.5));
    }
}
